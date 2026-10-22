using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
namespace CarWise.Api.Domain;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Missing JSON data");
}
public sealed record Employee
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Occupation { get; init; }
    public string Group { get; init; } = "";
    [JsonRequired] public decimal Salary { get; init; }
    [JsonRequired] public decimal Commission { get; init; }
    [JsonRequired] public decimal Bonus { get; init; }
    [JsonRequired] public decimal Pension { get; init; }
    [JsonRequired] public decimal Fund { get; init; }
    [JsonRequired] public decimal Credits { get; init; }
    [JsonRequired] public decimal Children { get; init; }
    [JsonRequired] public string? CurrentCar { get; init; }
    public string[]? KnownFields {get;init;}
    public AdvisorState? AdvisorState {get;init;}
    [JsonRequired] public decimal Participation { get; init; }
    public string CurrentModel { get; init; } = "";
    public string Question { get; init; } = "";
}
public sealed record CarPhoto(string Id, string View, string Credit = "");
public sealed record Car(string Id, string Group, string Brand, string MakerCode, string Model,
    string ModelCode, string Description, int Year, string Drive, decimal Price, decimal Value,
    decimal Cost, [property:JsonRequired] decimal Participation)
{
    public List<CarPhoto> Photos { get; init; } = [];
    public bool ParticipationKnown {get;init;}=true;
    public decimal OfficialValue { get; init; }
    public string[] OfficialKey { get; init; } = [];
}
public sealed record Occupation(string OccupationName, string Group, bool Mandatory, bool Waiver, decimal Maintenance);
public sealed record OfficialRecord(int TaxYear, int RegistrationYear, string VehicleType, string MakerCode,
    string ModelCode, decimal Value, string Drive, Dictionary<string, string> Document)
{
    public string Key => KeyFor(TaxYear, RegistrationYear, VehicleType, MakerCode, ModelCode);
    public static string KeyFor(int taxYear, int year, string type, string maker, string model) => $"{taxYear}/{year}/{type}/{maker}/{model}";
}
public record Amounts
{
    public decimal Net { get; init; }
    public decimal Tax { get; init; }
    public decimal Insurance { get; init; }
    public decimal Taxable { get; init; }
    public decimal Gross { get; init; }
    public decimal Pension { get; init; }
    public decimal Fund { get; init; }
    public decimal Value { get; init; }
    public decimal Participation { get; init; }
    public decimal FamilyGap { get; init; }
    public decimal Maintenance { get; init; }
    public decimal ElectricGross { get; init; }
    public decimal ElectricNet { get; init; }
    public decimal Budget { get; init; }
    public decimal Cost { get; init; }
}
public sealed record Alternative : Amounts
{
    public required string Id { get; init; }
    public Car? Car { get; init; }
    public bool Eligible { get; init; }
    public string Reason { get; init; } = "";
    public decimal Delta { get; init; }
    public decimal Annual { get; init; }
    public bool Pending { get; init; }
    public NetExplanation? Explanation { get; init; }
    public Alternative(Amounts a) : base(a) { }
    public Alternative() { }
}
public sealed record Simulation(Employee Employee, Amounts Current, IReadOnlyList<Alternative> Results);
public sealed record SimulateRequest(Employee Employee, Dictionary<string, decimal>? Overrides = null);
public sealed record TicketRequest(Employee Employee, string Target, string? Question);
public sealed record AnswerRequest(decimal Participation, string Answer);
public sealed record ChatRequest(Employee Employee, string Text, string? Target, Dictionary<string, decimal>? Overrides);
public sealed record Ticket(Guid Id, string EmployeeId, Employee Snapshot, string Target, string Question,
    string Status, string? Answer, decimal? Participation, DateTimeOffset Created);
public sealed record Message(string Role, string Text,IReadOnlyList<string>? Candidates=null);
public sealed record Conversation(Employee Employee, string Status, string? Target);
public sealed record DatabaseSnapshot(JsonNode Rules, List<Car> Cars, List<Employee> Employees,
    List<Dictionary<string, JsonElement>> Occupations, List<OfficialRecord> Official);
