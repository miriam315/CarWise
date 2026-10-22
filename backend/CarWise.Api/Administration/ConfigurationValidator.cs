using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Domain;
namespace CarWise.Api.Administration;

public sealed class ConfigurationValidator(ReferenceData officialSource)
{
    public ReferenceData Validate(ManagedConfiguration config,IReadOnlyDictionary<string,OfficialRecord>? selectedOfficial=null)
    {
        try
        {
            FleetRules.Validate(config.Fleet);
            IntentRules.Validate(config.IntentAnalytics);
            if(JsonDefaults.Serialize(config).Length>1_000_000)Fail("קובץ ההגדרות גדול מדי");
            if(config.Cars.Count is <1 or >500 || config.Occupations.Count is <1 or >500 || config.Employees.Count is <1 or >5000)
                Fail("נדרשים קטלוג, עיסוקים ועובדים; הרשימות חורגות מהגודל המותר");
            if(config.Employees.Any(e=>string.IsNullOrWhiteSpace(e.Name)))Fail("יש להזין שם לכל פרופיל עובד לפני שמירה");
            Unique(config.Cars.Select(c=>c.Id),"מזהי רכב");Unique(config.Employees.Select(e=>e.Id),"מזהי עובד");
            foreach(var car in config.Cars)
            {
                if(car.Photos is null || car.Photos.Count>60 || car.Photos.Any(p=>p is null || !CarImages.ValidId(p.Id??"") || p.View is not ("exterior" or "interior" or "spin") || p.Credit is null || p.Credit.Length>1000))
                    Fail("גלריית רכב: עד 60 תמונות, מזהים תקינים וקרדיט עד 1,000 תווים.");
                if(car.Photos!.Count(p=>p.View=="spin")>48 || car.Photos!.Select(p=>p.View+":"+p.Id).Distinct().Count()!=car.Photos!.Count)
                    Fail("סדרת 360° מוגבלת ל־48 צילומים שונים; אין לשכפל תמונה באותה קבוצת תצוגה.");
            }
            Unique(config.Occupations.Select(o=>o["occupation"].GetString()!),"עיסוקים");
            var rules=config.Rules;var groups=rules["groupOrder"]!.AsArray().Select(x=>x!.GetValue<string>()).ToList();
            if(groups.Count is <1 or >10)Fail("מספר קבוצות זכאות לא תקין");Unique(groups,"קבוצות זכאות");
            foreach(var group in groups)Range(rules["budgets"]![group],0,100000,"תקציב קבוצה");
            OneOf(rules["chooseFrom"],"own_group_or_lower","own_group_only");
            Range(rules["electric"]!["netBonus"],0,5000,"החזר חשמל");OneOf(rules["electric"]!["method"],"gross_up","gross");
            Range(rules["familyTrack"]!["minChildren"],0,30,"סף ילדים");
            Range(rules["familyTrack"]!["childMaxAge"],0,30,"גיל ילד מרבי");
            if(rules["familyTrack"]!["minChildren"]!.GetValue<decimal>()%1!=0 || rules["familyTrack"]!["childMaxAge"]!.GetValue<decimal>()%1!=0)Fail("ספי גיל ומספר ילדים חייבים להיות שלמים");
            Range(rules["familyTrack"]!["gapSplitEmployee"],0,1,"חלק העובד בפער");
            OneOf(rules["familyTrack"]!["priceSource"],"catalogue");OneOf(rules["waiver"]!["allowedBy"],"occupation");
            _=rules["waiver"]!["maintenanceIsTaxable"]!.GetValue<bool>();_=rules["waiver"]!["maintenanceIsPensionable"]!.GetValue<bool>();
            OneOf(rules["unusedBudget"],"not_paid");
            var net=rules["netMethod"]!;OneOf(net["basis"],"monthly");OneOf(net["contributionsBase"],"gross","base");
            Range(net["taxYear"],2000,2100,"שנת לוחות מס");Range(net["creditPointValue"],0,10000,"שווי נקודת זיכוי");
            OneOf(net["participation"]!["treatment"],"net_deduction");
            if(net["participation"]!["reducesValueOfUse"]!.GetValue<bool>())Fail("השתתפות אינה מקטינה שווי שימוש במנוע זה");
            OneOf(net["rounding"]!["during"],"none");OneOf(net["rounding"]!["display"],"shekel");
            var brackets=net["brackets"]!.AsArray();if(brackets.Count is <1 or >20)Fail("נדרשת לפחות מדרגת מס אחת");
            decimal previous=0;
            for(var i=0;i<brackets.Count;i++)
            {
                var bracket=brackets[i]!.AsArray();if(bracket.Count!=2)Fail("מדרגת מס חייבת לכלול תקרה ושיעור");
                Range(bracket[1],0,0.95m,"שיעור מס");
                if(bracket[0] is null){if(i!=brackets.Count-1)Fail("רק המדרגה האחרונה יכולה להיות ללא תקרה");}
                else {var ceiling=Range(bracket[0],0,10000000,"תקרת מס");if(ceiling<=previous)Fail("תקרות המס חייבות לעלות");previous=ceiling;}
            }
            if(brackets[^1]![0] is not null)Fail("מדרגת המס האחרונה חייבת להיות ללא תקרה");
            foreach(var name in new[]{"nationalInsurance","health"})
            {
                var rule=net[name]!;var threshold=Range(rule["threshold"],0,1000000,"סף ביטוח");
                if(Range(rule["ceiling"],1,10000000,"תקרת ביטוח")<threshold)Fail("תקרת ביטוח נמוכה מהסף");
                Range(rule["lowRate"],0,.5m,"שיעור ביטוח");Range(rule["highRate"],0,.5m,"שיעור ביטוח");
            }
            // The gross-up must remain monotone at all combined tax/insurance rates.
            var maxTax=brackets.Max(b=>b![1]!.GetValue<decimal>());
            var maxInsurance=new[]{"nationalInsurance","health"}.Sum(name=>Math.Max(net[name]!["lowRate"]!.GetValue<decimal>(),net[name]!["highRate"]!.GetValue<decimal>()));
            if(maxTax+maxInsurance>=.95m)Fail("סך שיעורי המס והביטוח גבוה מדי לחישוב גילום יציב");
            Range(net["pensionCredit"]!["rate"],0,1,"זיכוי פנסיה");Range(net["pensionCredit"]!["maxContributionRate"],0,1,"תקרת שיעור פנסיה");
            Range(net["pensionCredit"]!["incomeCeiling"],0,1000000,"הכנסה מזכה");
            var mapping=officialSource.Rules["valueOfUse"]!.DeepClone();
            if(selectedOfficial is not null&&config.OfficialVersionId!="baseline")
            {var selectedYear=selectedOfficial.Values.First().TaxYear;mapping["taxYear"]=selectedYear;mapping["source"]=mapping["source"]!.GetValue<string>().Replace(officialSource.TaxYear.ToString(),selectedYear.ToString(),StringComparison.Ordinal);}
            else if(selectedOfficial is null&&config.OfficialVersionId!="baseline")Fail("יש לטעון את גרסת המאגר לפני אימות ההגדרות");
            if(!JsonNode.DeepEquals(rules["valueOfUse"],mapping))Fail("מיפוי המאגר הרשמי אינו ניתן להחלפה באמצעות הגדרות שכר");
            var family=rules["familyTrack"]!["cars"]!.AsArray().Select(c=>c!.GetValue<string>()).ToHashSet();
            foreach(var id in family)if(!config.Cars.Any(c=>c.Id==id&&c.Group=="משפחתי"))Fail("רכב משפחתי המוגדר בכללים חסר בקטלוג");
            foreach(var car in config.Cars)
            {
                if(!Regex.IsMatch(car.Id,@"^[A-Za-z0-9_-]{1,80}$") || string.IsNullOrWhiteSpace(car.Model))Fail("מזהה או שם רכב לא תקינים");
                if(!groups.Contains(car.Group)&&car.Group!="משפחתי")Fail("קבוצת רכב אינה מוגדרת");
                if(car.Group=="משפחתי"&&!family.Contains(car.Id))Fail("יש לכלול את הרכב המשפחתי גם בכללי המסלול");
                if(car.Cost<0 || car.Cost>100000 || car.Participation<0 || car.Participation>20000 || car.Price<0)Fail("סכומי רכב מחוץ לטווח");
            }
            foreach(var document in config.Occupations)
            {
                if(!groups.Contains(document["group"].GetString()!))Fail("קבוצת עיסוק אינה מוגדרת");
                if(document["mandatory"].GetBoolean()&&document["waiver"].GetBoolean())Fail("עיסוק עם רכב חובה אינו יכול לאפשר ויתור");
                var maintenance=document["maintenance"].GetDecimal();if(maintenance<0 || maintenance>30000)Fail("אחזקת רכב מחוץ לטווח");
                if(!document["waiver"].GetBoolean()&&maintenance!=0)Fail("עיסוק שאינו מאפשר ויתור חייב באחזקה אפס");
            }
            var data=ReferenceData.FromSnapshot(new(rules.DeepClone(),config.Cars,config.Employees,config.Occupations,(selectedOfficial??officialSource.Official).Values.ToList()));
            var engine=new PayrollEngine(data);var employees=data.Employees.Select(engine.Validate).ToList();
            if(new[]{config.Ai.ExtractionPrompt,config.Ai.NarrationPrompt,config.Ai.PayslipPrompt}.Any(p=>p is null||p.Length>12000))Fail("פרומפט מוגבל ל־12,000 תווים");
            if(config.Ai.Models is null||config.Ai.Models.Count>20||config.Ai.ServiceModels is null)Fail("קטלוג מודלים לא תקין; עד 20 מודלים");
            if(config.Ai.Models!.Count>0)
            {
                Unique(config.Ai.Models.Select(m=>m.Id),"מודלי AI");
                foreach(var model in config.Ai.Models)
                    if(!Regex.IsMatch(model.Id,@"^[a-z0-9-]{1,50}$")||string.IsNullOrWhiteSpace(model.Name)||model.Name.Length>100)Fail("שם או מזהה מודל לא תקין");
                if(config.Ai.ServiceModels!.Count is <2 or >4||config.Ai.ServiceModels.Keys.Any(key=>key is not ("extraction" or "narration" or "payslip" or "insights"))||new[]{"extraction","narration"}.Any(service=>!config.Ai.ServiceModels.TryGetValue(service,out var id)||!config.Ai.Models.Any(m=>m.Id==id))||config.Ai.ServiceModels.Any(binding=>binding.Key!="payslip"&&!config.Ai.Models.Any(m=>m.Id==binding.Value)))Fail("יש לבחור מודל קיים לכל שירות AI");
            }
            var ocr=config.Ai.PayslipOcr;
            if(ocr is null||ocr.Languages is not ("heb+eng" or "heb" or "eng")||ocr.MaxPages is <1 or >6||ocr.TimeoutSeconds is <10 or >120||ocr.MinimumConfidence is <50 or >100)
                Fail("הגדרות OCR מקומי: שפות heb+eng / heb / eng, עד 1–6 עמודים, זמן המתנה 10–120 שניות וסף זיהוי 50–100");
            foreach(var model in config.Ai.RegisteredModels())
                try{ValidateAi(model.Profile);}
                catch(ArgumentException error){throw new ArgumentException($"מודל ״{model.Name}״ ({model.Id}): {error.Message}");}
            if(config.Ai.Pricing is null || config.Ai.Pricing.Count>200 || config.Ai.ExchangeRateUpdateHourUtc is <0 or >23)Fail("הגדרות תמחור או שעת עדכון אינן תקינות");
            var priceKeys=new HashSet<string>();
            foreach(var price in config.Ai.Pricing!)
            {
                var registered=config.Ai.RegisteredModels().Select(m=>m.Profile).FirstOrDefault(p=>p.CustomProvider&&p.Provider==price.Provider&&new Uri(p.BaseUrl).Host==price.Host);
                ValidateAi(registered is null?new AiProfile{Provider=price.Provider,BaseUrl="https://"+price.Host+"/",Model=price.Model}:registered with{Model=price.Model});
                if(price.Host!=new Uri("https://"+price.Host+"/").Host)Fail("דומיין תמחור אינו תקין");
                if(!priceKeys.Add(price.Provider+"/"+price.Host+"/"+price.Model))Fail("תמחור כפול לאותו מודל וספק");
                if(new[]{price.InputUsdPerMillion,price.CachedInputUsdPerMillion,price.OutputUsdPerMillion,price.CacheWriteUsdPerMillion??0}.Any(v=>v<0||v>100000)
                    ||price.CachedInputUsdPerMillion>price.InputUsdPerMillion||string.IsNullOrWhiteSpace(price.Source)||price.Source.Length>500)Fail("תעריף מודל או מקור אינו תקין");
            }
            return new ReferenceData{Rules=data.Rules,Cars=data.Cars,Employees=employees,OccupationDocuments=data.OccupationDocuments,Official=data.Official,OfficialRowCount=config.OfficialVersionId=="baseline"?officialSource.OfficialRowCount:data.OfficialRowCount};
        }
        catch(Exception error) when(error is InvalidOperationException or KeyNotFoundException or NullReferenceException or InvalidDataException or FormatException)
        {throw new ArgumentException("מבנה הגדרות לא תקין או אי־התאמה למאגר הרשמי. בדקו שדות, קודים וסכומים.");}
    }
    public static void ValidateAi(AiProfile profile)
    {
        if(profile.DisplayName is null||profile.DisplayName.Length>100)Fail("שם ספק ארוך מדי");
        if(profile.Provider is not ("openai" or "compatible" or "anthropic" or "gemini"))Fail("ספק AI לא נתמך");
        if(string.IsNullOrWhiteSpace(profile.Model)||profile.Model.Length>160||!Regex.IsMatch(profile.Model,@"^[A-Za-z0-9_./:@-]+$"))Fail("שם מודל לא תקין");
        if(profile.MaxOutputTokens is <128 or >8192)Fail($"מגבלת הפלט היא {profile.MaxOutputTokens}; יש להזין בין 128 ל־8,192 טוקנים");
        if(profile.TimeoutSeconds is <5 or >90)Fail($"זמן ההמתנה הוא {profile.TimeoutSeconds}; יש להזין בין 5 ל־90 שניות");
        if(!Uri.TryCreate(profile.BaseUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))Fail("כתובת AI חייבת להיות HTTPS ללא פרטי גישה, פורט או פרמטרים");
        if(profile.CustomProvider&&(!profile.CustomEndpointApproved||string.IsNullOrWhiteSpace(profile.DisplayName)))Fail("ספק ידני דורש שם ואישור מפורש של כתובת ה־API");
        if(profile.CustomProvider&&!CarWise.Api.AI.PublicAiConnection.ValidHost(uri!.Host))Fail("כתובת ספק ידני חייבת להיות דומיין ציבורי, ללא כתובות IP או שמות פנימיים");
        var allowed=new Dictionary<string,string[]>{["openai"]=["api.openai.com"],["anthropic"]=["api.anthropic.com"],["gemini"]=["generativelanguage.googleapis.com"],
            ["compatible"]=["api.openai.com","api.groq.com","api.mistral.ai","openrouter.ai","api.deepseek.com","api.together.xyz","api.fireworks.ai"]};
        var custom=(Environment.GetEnvironmentVariable("CARWISE_AI_ALLOWED_HOSTS")??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var customAllowed=profile.Provider=="compatible"&&custom.Contains(uri!.Host,StringComparer.OrdinalIgnoreCase)&&Uri.CheckHostName(uri.Host)==UriHostNameType.Dns&&!uri.IsLoopback;
        if(!profile.CustomProvider&&!customAllowed&&!allowed[profile.Provider].Contains(uri!.Host,StringComparer.OrdinalIgnoreCase))Fail("הדומיין אינו ברשימת ספקי ה־AI המותרים. אין לשלוח מפתחות לכתובת שרירותית");
        if(profile.BackupKeys is null||profile.BackupKeys.Count>7||profile.BackupKeys.Any(k=>k is null))Fail("ניתן להגדיר עד שבעה מפתחות גיבוי בנוסף לראשי");
        if(profile.BackupKeys!.Select(k=>k.Id).Distinct().Count()!=profile.BackupKeys!.Count)Fail("מזהי מפתחות כפולים");
        foreach(var key in profile.BackupKeys!)
            if(key.Id is null||!Regex.IsMatch(key.Id,@"^[a-f0-9]{16}$")||string.IsNullOrWhiteSpace(key.Name)||key.Name.Length>80||key.Name.Any(char.IsControl))Fail("שם או מזהה מפתח גיבוי לא תקין");
        foreach(var key in profile.Keys())
            if(key.KeyReference is null||!Regex.IsMatch(key.KeyReference,@"^(environment:CARWISE_AI_[A-Z0-9_]+|vault:[a-f0-9]{32})$"))Fail("הפניה למפתח אינה תקינה");
    }
    private static decimal Range(JsonNode? value,decimal min,decimal max,string name)
    {var number=value?.GetValue<decimal>()??throw new ArgumentException("חסר שדה: "+name);if(number<min||number>max)Fail(name+" מחוץ לטווח");return number;}
    private static void Unique(IEnumerable<string> values,string name)
    {var list=values.ToList();if(list.Any(string.IsNullOrWhiteSpace)||list.Distinct().Count()!=list.Count)Fail("ערכים חסרים או כפולים: "+name);}
    private static void OneOf(JsonNode? node,params string[] options)
    {if(node is null||!options.Contains(node.GetValue<string>()))Fail("ערך כלל אינו נתמך");}
    private static void Fail(string message)=>throw new ArgumentException(message);
}
