using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using CarWise.Api.Domain;
namespace CarWise.Api.Infrastructure;

public interface IJsonDatabase
{
    string Provider { get; }
    Task<JsonArray> ReadAsync(string table, IReadOnlyDictionary<string,string>? filters=null, int offset=0, int limit=1000, CancellationToken ct=default,string? afterId=null);
    Task UpsertAsync(string table, JsonArray rows, CancellationToken ct=default);
    Task<JsonNode> RpcAsync(string name, JsonObject parameters, CancellationToken ct=default);
    Task AddMessageAsync(string employee,string role,string text,CancellationToken ct=default);
}
public sealed class PostgresDatabase(NpgsqlDataSource source) : IJsonDatabase
{
    public string Provider => "PostgreSQL";
    private static readonly HashSet<string> Tables=["carwise_rules","carwise_cars","carwise_occupations","carwise_employees",
        "carwise_official","carwise_tickets","carwise_messages","carwise_conversations"];
    private static string Table(string name) => Tables.Contains(name)?name:throw new ArgumentException("Unknown table");
    public async Task MigrateAsync(string path,CancellationToken ct=default)
    {
        await using var conn=await source.OpenConnectionAsync(ct);
        await using var transaction=await conn.BeginTransactionAsync(ct);
        await using var command=new NpgsqlCommand(File.ReadAllText(path),conn,transaction);
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
    }
    public async Task<JsonArray> ReadAsync(string table,IReadOnlyDictionary<string,string>? filters=null,int offset=0,int limit=1000,CancellationToken ct=default,string? afterId=null)
    {
        var clauses=new List<string>();await using var conn=await source.OpenConnectionAsync(ct);
        await using var command=conn.CreateCommand();
        if(filters is not null) foreach(var (key,value) in filters)
        {
            if(key is not ("employee_id" or "status" or "id"))throw new ArgumentException("Unknown filter");
            var parameter="p"+clauses.Count;clauses.Add($"t.{key}::text=@{parameter}");command.Parameters.AddWithValue(parameter,value);
        }
        if(afterId is not null){clauses.Add("t.id > @after");command.Parameters.AddWithValue("after",afterId);}
        command.CommandText=$"SELECT to_jsonb(t)::text FROM {Table(table)} t"+(clauses.Count>0?" WHERE "+string.Join(" AND ",clauses):"")+" ORDER BY t."+(table=="carwise_conversations"?"employee_id":"id")+" LIMIT @limit OFFSET @offset";
        command.Parameters.AddWithValue("limit",limit);command.Parameters.AddWithValue("offset",offset);
        var rows=new JsonArray();await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(JsonNode.Parse(reader.GetString(0)));
        return rows;
    }
    public async Task UpsertAsync(string table,JsonArray rows,CancellationToken ct=default)
    {
        Table(table);
        if(table is not ("carwise_rules" or "carwise_cars" or "carwise_occupations" or "carwise_employees" or "carwise_official"))throw new ArgumentException("Not reference data");
        await using var command=source.CreateCommand($"INSERT INTO {table}(id,document) SELECT id,document FROM jsonb_to_recordset(@rows) AS x(id text,document jsonb) ON CONFLICT(id) DO UPDATE SET document=excluded.document");
        command.Parameters.AddWithValue("rows",NpgsqlDbType.Jsonb,rows.ToJsonString());await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<JsonNode> RpcAsync(string name,JsonObject parameters,CancellationToken ct=default)
    {
        if(name is not ("carwise_create_ticket" or "carwise_resolve_ticket" or "carwise_save_conversation"))throw new ArgumentException("Unknown RPC");
        await using var command=source.CreateCommand();var parts=new List<string>();
        foreach(var (key,value) in parameters)
        {
            if(key is not ("p_employee" or "p_target" or "p_question" or "p_id" or "p_participation" or "p_answer" or "p_message"))throw new ArgumentException("Unknown RPC argument");
            parts.Add($"{key} => @{key}");
            if(key=="p_employee") command.Parameters.AddWithValue(key,NpgsqlDbType.Jsonb,value!.ToJsonString());
            else if(key=="p_id")command.Parameters.AddWithValue(key,NpgsqlDbType.Uuid,Guid.Parse(value!.GetValue<string>()));
            else if(key=="p_participation")command.Parameters.AddWithValue(key,NpgsqlDbType.Numeric,value!.GetValue<decimal>());
            else command.Parameters.AddWithValue(key,NpgsqlDbType.Text,(object?)value?.GetValue<string>()??DBNull.Value);
        }
        command.CommandText=$"SELECT {name}({string.Join(',',parts)})::text";
        return JsonNode.Parse((string)(await command.ExecuteScalarAsync(ct)??"{}"))!;
    }
    public async Task AddMessageAsync(string employee,string role,string text,CancellationToken ct=default)
    {
        await using var command=source.CreateCommand("INSERT INTO carwise_messages(employee_id,role,text) VALUES(@e,@r,@t)");
        command.Parameters.AddWithValue("e",employee);command.Parameters.AddWithValue("r",role);command.Parameters.AddWithValue("t",text);await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Supabase PostgREST access. The service-role key stays in this server's HttpClient.</summary>
public sealed class SupabaseDatabase(HttpClient client) : IJsonDatabase
{
    public string Provider => "Supabase/PostgreSQL";
    private async Task<JsonNode> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        using(request)using(var response=await client.SendAsync(request,ct))
        {
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Supabase request failed ({(int)response.StatusCode}). Check schema and server configuration.");
            var raw=await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(raw)?new JsonArray():JsonNode.Parse(raw)!;
        }
    }
    public async Task<JsonArray> ReadAsync(string table,IReadOnlyDictionary<string,string>? filters=null,int offset=0,int limit=1000,CancellationToken ct=default,string? afterId=null)
    {
        var query=$"rest/v1/{Uri.EscapeDataString(table)}?select=*&order="+(table=="carwise_conversations"?"employee_id":"id")+$".asc&offset={offset}&limit={limit}";
        if(afterId is not null)query+="&id=gt."+Uri.EscapeDataString(afterId);
        if(filters is not null)foreach(var (key,value) in filters)query+=$"&{Uri.EscapeDataString(key)}=eq.{Uri.EscapeDataString(value)}";
        return (await SendAsync(new(HttpMethod.Get,query),ct)).AsArray();
    }
    public async Task UpsertAsync(string table,JsonArray rows,CancellationToken ct=default)
    {
        var request=new HttpRequestMessage(HttpMethod.Post,$"rest/v1/{table}?on_conflict=id"){Content=JsonContent.Create(rows)};
        request.Headers.TryAddWithoutValidation("Prefer","resolution=merge-duplicates,return=minimal");await SendAsync(request,ct);
    }
    public Task<JsonNode> RpcAsync(string name,JsonObject parameters,CancellationToken ct=default) =>
        SendAsync(new(HttpMethod.Post,$"rest/v1/rpc/{name}"){Content=JsonContent.Create(parameters)},ct);
    public async Task AddMessageAsync(string employee,string role,string text,CancellationToken ct=default) =>
        await SendAsync(new(HttpMethod.Post,"rest/v1/carwise_messages"){Content=JsonContent.Create(new{employee_id=employee,role,text})},ct);
}
