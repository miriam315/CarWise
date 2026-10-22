using System.Text.Json;
using System.Text.Json.Nodes;
namespace CarWise.Api.Domain;

public static class SimulationView
{
    private static readonly string[] Numbers=["net","delta","annual","tax","insurance","taxable","gross","pension","fund","value","participation","familyGap","maintenance","electricGross","electricNet"];
    public static JsonNode Incomplete(Employee employee)=>new JsonObject{
        ["employee"]=JsonSerializer.SerializeToNode(employee,JsonDefaults.Options),["current"]=null,["results"]=new JsonArray(),
        ["complete"]=false,["missingFields"]=JsonSerializer.SerializeToNode(IntakeFields.Missing(employee)),
        ["message"]="חסרים נתונים. אפשר להשלים אותם בשיחה או בטופס; עדיין אין חישוב נטו."};
    public static JsonNode Complete(Simulation simulation)
    {
        var result=JsonSerializer.SerializeToNode(simulation,JsonDefaults.Options)!;
        result["complete"]=true;result["missingFields"]=new JsonArray();
        foreach(var item in result["results"]!.AsArray())
            if(item!["pending"]!.GetValue<bool>()||!item["eligible"]!.GetValue<bool>())
            {foreach(var key in Numbers)item[key]=null;item["explanation"]=null;}
        return result;
    }
}
public sealed record AdvisorTiming(TimeSpan Total,TimeSpan Extraction,TimeSpan Narration)
{
    public static AdvisorTiming Default=>new(TimeSpan.FromSeconds(50),TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(12));
    public AdvisorTiming Validate()
    {
        if(Total<=TimeSpan.Zero||Total>Default.Total||Extraction<=TimeSpan.Zero||Extraction>Default.Extraction||Narration<=TimeSpan.Zero||Narration>Default.Narration)throw new ArgumentOutOfRangeException(nameof(Total));
        return this;
    }
}
