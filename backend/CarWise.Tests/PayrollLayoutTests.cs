using CarWise.Api.AI;
using Xunit;
namespace CarWise.Tests;

public sealed class PayrollLayoutTests
{
    private static PayrollWord W(string text,double x,double y,double width=40,double confidence=100,int page=0)=>new(text,x,y,width,10,confidence,page);
    private static PayslipResult Extract(params PayrollWord[] words)=>LocalPayrollLayout.Extract(words,75,1,"local-test");
    private static PayrollWord[] Earnings(string salary="14,364.00")=>[
        W("סכום",20,20),W("תעריף",100,20),W("כמות",180,20),W("תיאור",270,20),W("רכיב",245,20,20),W("קוד",360,20),
        W(salary,10,45,60),W("85.50",100,45),W("168.00",180,45),W("שכר",280,45,20),W("יסוד",250,45,25),W("001",360,45)];
    [Fact] public void HourlySalaryComesFromAmountColumnNeverRateQuantityOrCode()
    {Assert.Equal(14364,Extract(Earnings()).Fields["salary"].Value);}
    [Theory][InlineData(0)][InlineData(230)]
    public void AdjacentDeductionTableCannotContaminateEarningsRegardlessOfPlacement(int offset)
    {
        var words=Earnings().Select(w=>w with{X=w.X+offset}).ToList();
        var dx=offset==0?450:0;
        words.AddRange([W("סכום",dx+10,20),W("תיאור",dx+100,20),W("קוד",dx+180,20),
            W("999.00",dx+10,45),W("מס",dx+110,45,15),W("הכנסה",dx+70,45,35),W("101",dx+180,45)]);
        Assert.Equal(14364,Extract(words.ToArray()).Fields["salary"].Value);
    }
    [Fact] public void EmployeePercentageIsReadFromNamedColumnNotEmployerAmountOrAccumulation()
    {
        var r=Extract(W("מצטבר",10,20),W("אחוז",120,20,25),W("מעסיק",80,20,35),W("סכום",220,20,25),W("עובד",185,20,30),
            W("אחוז",320,20,25),W("עובד",285,20,30),W("בסיס",390,20),W("קופה",480,20),
            W("9,000",10,45),W("6.5%",90,45),W("860",195,45),W("6%",300,45),W("14364",390,45),W("פנסיה",480,45));
        Assert.Equal(6,r.Fields["pension"].Value);Assert.Null(r.Fields["salary"].Value);
    }
    [Fact] public void CreditsArePairedVerticallyWithTheirHeading()
    {
        var r=Extract(W("שווי",10,20,20),W("נקודה",35,20,30),W("נקודות",180,20,35),W("זיכוי",140,20,30),W("אחוז",280,20,25),W("משרה",240,20,30),
            W("242.00",15,45),W("2.75",155,45),W("100%",250,45));
        Assert.Equal(2.75m,r.Fields["credits"].Value);
    }
    [Theory][InlineData("מצטבר")][InlineData("מעסיק")][InlineData("ברוטו")][InlineData("קודם")]
    public void LongerDescriptionCannotBeShortenedToBaseSalary(string extra)
    {var words=Earnings().Concat([W(extra,215,45,30)]).ToArray();Assert.Null(Extract(words).Fields["salary"].Value);}
    [Fact] public void UnknownOrNegativeAmountNeverFallsBackToHourlyRate()
    {
        Assert.Null(Extract(Earnings("-14,364.00")).Fields["salary"].Value);
        Assert.Null(Extract(Earnings("unreadable")).Fields["salary"].Value);
        Assert.Null(Extract(Earnings().Select(w=>w.Text=="14,364.00"?w with{Confidence=74}:w).ToArray()).Fields["salary"].Value);
    }
    [Fact] public void MissingHeadingAndConflictingRowsRemainUnknown()
    {
        Assert.Null(Extract(Earnings().Where(w=>w.Y>20).ToArray()).Fields["salary"].Value);
        var duplicate=Earnings().Where(w=>w.Y>20).Select(w=>w with{Y=75,Text=w.Text=="14,364.00"?"14000":w.Text});
        Assert.Null(Extract(Earnings().Concat(duplicate).ToArray()).Fields["salary"].Value);
    }
    [Fact] public void SeparatePagesCannotPairHeaderAndAmount()
    {Assert.Null(Extract(Earnings().Select(w=>w.Y>20?w with{Page=1}:w).ToArray()).Fields["salary"].Value);}
    [Fact] public void VisualHebrewAndBidiControlsAreNormalizedWithoutReversingNumbers()
    {
        var words=Earnings().Select(w=>w with{Text=new string(w.Text.Any(c=>c>='א'&&c<='ת')?w.Text.Reverse().ToArray():w.Text.ToArray())}).ToArray();
        Assert.Equal(14364,Extract(words).Fields["salary"].Value);
    }
    [Fact] public void ConflictingPassCannotBeOverwrittenByLaterPass()
    {
        var conflict=LocalPayrollFields.Extract([new("שכר בסיס: 18000"),new("שכר בסיס: 19000")],75,1,"local-test");
        var good=LocalPayrollFields.Extract([new("שכר בסיס: 18000")],75,1,"local-test");
        var merged=LocalPayrollFields.Merge([conflict,good],1,"local-test");
        Assert.Null(LocalPayrollFields.Merge([merged,good],1,"local-test").Fields["salary"].Value);
    }
    [Fact] public void HebrewOnlyLabelRepairCannotChangeAmountsEvenWhenTheirConfidenceIsHigh()
    {
        var original=new[]{W("Tay",120,40),W("350.00",20,40),W("שכר",200,40)};
        var alternate=new[]{W("עובד",120,40),W("20",20,40),W("בסיס",200,40)};
        var repaired=LocalPayrollLayout.WithHebrewLabels(original,alternate,75);
        Assert.Equal("עובד",repaired[0].Text);Assert.Equal("350.00",repaired[1].Text);Assert.Equal("שכר",repaired[2].Text);
        Assert.Equal("Tay",LocalPayrollLayout.WithHebrewLabels(original,[W("עובד",121,40,40,74)],75)[0].Text);
    }
    [Fact] public void SmallDashDoesNotSplitAnOtherwiseAlignedFundLabel()
    {
        var result=Extract(W("קופה",450,20),W("אחוז",300,20,25),W("עובד",265,20,30),
            W("קרן",475,44,20),W("השתלמות",420,40,50),new("—",397,48,15,2),W("2.5",275,45));
        Assert.Equal(2.5m,result.Fields["fund"].Value);
    }
    [Fact] public void FlatteningOcrBlocksCannotTurnAQuantityIntoSalary()
    {
        // The salary label and quantity may share an OCR line ID while the real
        // amount has another block ID. Geometry, not that ID, defines the row.
        var lines="level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"+
            "5\t1\t1\t1\t1\t1\t280\t45\t20\t10\t95\tשכר\n"+
            "5\t1\t1\t1\t1\t2\t250\t45\t25\t10\t95\tבסיס\n"+
            "5\t1\t1\t1\t1\t3\t180\t45\t40\t10\t95\t1.00\n"+
            "5\t1\t2\t1\t1\t1\t10\t45\t60\t10\t95\t12500.00\n";
        Assert.Null(LocalPayrollLayout.Extract(LocalPayrollLayout.ReadTsv(lines),75,1,"local-test").Fields["salary"].Value);
    }
    [Fact] public void PdfAndTsvParsersPreserveGeometryAndNeverReturnRawPersonalText()
    {
        var words=LocalPayrollLayout.ReadPdf("""<!DOCTYPE html SYSTEM "https://invalid.example/untrusted.dtd"><html><page><word xMin="10" xMax="20" yMin="30" yMax="40">SECRET-PERSON</word></page></html>""");
        var word=Assert.Single(words);Assert.Equal(10,word.X);Assert.Equal(30,word.Y);
        Assert.DoesNotContain("SECRET",System.Text.Json.JsonSerializer.Serialize(Extract(words.ToArray())));
        var tsv="level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n5\t1\t1\t1\t1\t1\t15\t25\t40\t10\t91\t2.75\n";
        var t=Assert.Single(LocalPayrollLayout.ReadTsv(tsv));Assert.Equal(15,t.X);Assert.Equal(91,t.Confidence);
    }
}
