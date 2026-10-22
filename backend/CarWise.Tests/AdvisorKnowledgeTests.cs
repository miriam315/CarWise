using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;
public sealed class AdvisorKnowledgeTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    [Theory][InlineData("לאיזה קבוצה אני שייכת")][InlineData("לאיזו קבוצה אני שייך?")][InlineData("מה הקבוצה שלי?")]
    public async Task GroupQuestionsAnswerFromOccupationEvenWithoutCompleteSalary(string text)
    {
        var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(k=>k!="salary")]};
        var db=new ConversationDatabase();var advisor=new AdvisorService(Data,new(Data),new(db),new LocalExtractionService(),new TemplateNarrationService());
        var response=JsonNode.Parse(JsonDefaults.Serialize(await advisor.ChatAsync(new(employee,text,null,null),default)))!;
        Assert.Contains(Data.OccupationFor(employee.Occupation).Group,response["reply"]!.ToString());
        Assert.Contains(employee.Occupation,response["reply"]!.ToString());Assert.Contains("מקור",response["reply"]!.ToString());
        Assert.DoesNotContain("מהו שכר",response["reply"]!.ToString());Assert.Equal(2,db.Messages.Count);
        Assert.False(response["complete"]!.GetValue<bool>());
    }
    [Fact] public void PersonalContributionIsNeverReplacedWithCatalogueDefault()
    {
        var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(k=>k!="participation")]};
        var answer=AdvisorKnowledge.Answer(["current_participation"],employee,Data,new(Data));
        Assert.Contains("עדיין אין",answer);Assert.Contains("לא אשתמש בברירת המחדל",answer);
        Assert.Empty(AdvisorKnowledge.Answer(["made_up_topic"],employee,Data,new(Data)));
    }
    [Theory]
    [InlineData("בשמחה. {FACT_0} רוצה לבדוק גם אילו רכבים מתאימים?",true)]
    [InlineData("התקציב שלך 99999",false)]
    [InlineData("{FACT_0} {FACT_0}",false)]
    [InlineData("{FACT_0} {OTHER}",false)]
    public async Task FactualNarrationMustPreserveServerFactsAndCannotAddNumbers(string template,bool valid)
    {
        using var http=new HttpClient(new FakeHttp(_=>new(HttpStatusCode.OK){Content=new StringContent(JsonDefaults.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=JsonDefaults.Serialize(new{text=template})}}}}))})){BaseAddress=new("https://example.invalid/v1/")};
        var narration=new AiNarrationService(new AiGateway(http,"SELECTED-MODEL"),NullLogger<AiNarrationService>.Instance);
        var fact="קבוצתך ב, והתקציב המאושר הוא 3200 ₪.";
        var result=await narration.ExplainFactsAsync([fact],default);
        Assert.Contains(fact,result);Assert.DoesNotContain("99999",result);
        Assert.Equal(valid?template.Replace("{FACT_0}",fact):fact,result);
    }
    [Fact] public async Task ContextualNarrationReceivesTheActualFollowupAndTrustedWorkflowWithoutChangingFinancialFacts()
    {
        var completion=new ContextCompletion();
        var narration=new AiNarrationService(completion,NullLogger<AiNarrationService>.Instance);
        var fact="ההשתתפות היא 350 ₪ לפי הקטלוג.";
        var answer=await narration.ComposeAsync(new("ומה לגבי הסכום הזה?",[new{role="assistant",text="דיברנו על הרכב הקודם"}],new{target="CAR-03",pending=(object?)null},[fact]),default);
        Assert.Equal(fact,answer);
        Assert.Equal("ומה לגבי הסכום הזה?",completion.Input!["conversation"]!["question"]!.ToString());
        Assert.Equal("CAR-03",completion.Input["conversation"]!["workflow"]!["target"]!.ToString());
        Assert.Single(completion.Input["conversation"]!["recentMessages"]!.AsArray());
        Assert.Equal(fact,completion.Input["facts"]![0]!["meaning"]!.ToString());
    }
    private sealed class ContextCompletion:IAiCompletionClient
    {
        public JsonNode? Input;
        public Task<string> CompleteAsync(string name,JsonNode schema,string instructions,string text,CancellationToken ct)
        {Input=JsonNode.Parse(text);return Task.FromResult("{\"text\":\"{FACT_0}\"}");}
    }
    [Fact] public async Task AiUnderstandsAQuestionWithoutAKeywordMatchAndUsesOnlyServerFacts()
    {
        using var http=new HttpClient(new FakeHttp(_=>new(HttpStatusCode.OK){Content=new StringContent(JsonDefaults.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=JsonDefaults.Serialize(new ExtractedData{QuestionTopics=["group","budget"]})}}}}))})){BaseAddress=new("https://example.invalid/v1/")};
        var advisor=new AdvisorService(Data,new(Data),new(new ConversationDatabase()),new AiExtractionService(new AiGateway(http,"SELECTED-INTENT")),new TemplateNarrationService());
        var response=JsonNode.Parse(JsonDefaults.Serialize(await advisor.ChatAsync(new(Data.Employees[0],"איך מסווגים אותי ומה מסגרת ההוצאה שלי?",null,null),default)))!;
        var answer=response["reply"]!.ToString();Assert.Contains("קבוצת הזכאות",answer);Assert.Contains("טבלת התקציבים",answer);
        Assert.DoesNotContain("אפשר להשוות רכב מסוים",answer);
    }
}
