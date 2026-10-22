# פריסה ל־Railway עם Supabase קיים

הפריסה היא שירות Web אחד: React נבנה מראש, ו־ASP.NET Core מגיש את האתר ואת ה־API. משתמשים בפרויקט Supabase הקיים; לא מוסיפים PostgreSQL ב־Railway. אין צורך להתקין Docker במחשב לצורך פריסה מה־GitHub. עיבוד התלושים הפעיל אינו משתמש במנוע OCR היסטורי או ב־Docker פנימי.

## לפני הפריסה

- ודאו שב־Supabase קיימים טבלאות CarWise, המיגרציות ונתוני המערכת. אם משתמשים באותו פרויקט שעובד במחשב, אין לייבא שוב את הנתונים או להריץ איפוס.
- ודאו שניתן להתחבר במחשב כמנהל עם דוא״ל וסיסמה. יצירת המנהל הראשון מוגבלת למחשב השרת במצב Development ואינה פתוחה באתר הענן. משתמשים באותו חשבון בענן, כי החשבונות והשייכויות נשמרים ב־Supabase.
- הכינו את מפתח השרת של Supabase: מפתח secret או service_role, לא publishable/anon. אין להכניס אותו ל־GitHub או למשתנה VITE.

## הקמה

1. היכנסו ל־Railway עם GitHub ובחרו New Project → Deploy from GitHub repo → `miriam315/CarWise`. אשרו גישה לריפו אם הוא פרטי. בחרו branch `main` ו־Root Directory של שורש הריפו (`/`).
2. קובצי `Dockerfile` ו־`railway.json` קובעים את הבנייה וההפעלה. השאירו שדות Build Command ו־Start Command מותאמים ריקים; אין להשתמש ב־`npm start` בענן. הפריסה הראשונה לא תהיה מוכנה לפני הגדרת משתני הסביבה והדיסק.
3. בשירות → Variables הזינו:

   | שם | ערך |
   | --- | --- |
   | `SUPABASE_URL` | `https://dfcgppnbpmueugcwemrj.supabase.co` |
   | `CARWISE_SUPABASE_KEY` | מפתח השרת של אותו פרויקט |
   | `ASPNETCORE_ENVIRONMENT` | `Production` |
   | `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` |
   | `PORT` | `8000` |

   משתנה forwarded headers מיועד לשירות שמוגש מאחורי פרוקסי Railway, כדי לזהות את HTTPS החיצוני. אין להפעיל מצב Development, DemoMode או `CARWISE_ALLOW_LOCAL_SETUP`.
4. הוסיפו לשירות Volume דרך תפריט השירות/משטח הפרויקט. קבעו Mount Path: **`/var/carwise`**. קובץ Docker כבר מגדיר שמירת מפתחות ההצפנה ב־`/var/carwise/admin-keys` והגדרות החיבור ב־`/var/carwise/server-settings/supabase.protected.json`. אין לשנות את נתיב החיבור או להפעיל שירות ללא ה־Volume לאחר שהוזנו בו מפתחות AI.
5. השאירו Replica אחת. אם קיימת אפשרות Serverless/Sleep, כבו אותה לקראת ההדגמה כדי למנוע המתנה להתעוררות. בחרו Region קרוב ככל האפשר ל־Region של Supabase.
6. הפעילו Deploy/Apply Changes/Redeploy והמתינו לסיום הבנייה. בדיקת המוכנות מוגדרת ב־`/api/health` עם תקרה של 300 שניות. היא רשאית להשתמש ב־HTTP פנימי; שאר בקשות ה־API ב־Production עדיין דורשות HTTPS.
7. בשירות → Settings → Networking → Public Networking → Generate Domain. בחרו Target Port **8000**. Railway נותנת כתובת HTTPS תחת `up.railway.app`; לא נדרשת רכישת דומיין. אפשר לחבר דומיין פרטי בהמשך.
8. אחרי יצירת הדומיין, הגדירו `AllowedHosts` לערך `YOUR-DOMAIN.up.railway.app;healthcheck.railway.app` והחילו את השינוי. מחליפים את שם הדומיין בדומיין בפועל, ללא `https://` וללא נתיב. אם מוסיפים דומיין פרטי, מוסיפים אותו לאותה רשימה עם `;`.

## פתיחה והגדרת AI

פתחו `https://YOUR-DOMAIN.up.railway.app/api/health`: צריכה להתקבל תשובת JSON עם `status: "ok"`. ב־`/api/runtime` ודאו `authenticationRequired: true`, `demoMode: false`, `requiresSetup: false`. עברו לכתובת הראשית והתחברו בחשבון המנהל הקיים.

מפתחות AI שנשמרו במחשב Windows מוצפנים במפתחות של אותו מחשב, שחלקם מוגנים ב־DPAPI. עצם השימוש באותו Supabase אינו מאפשר לשרת Linux החדש לפענח אותם. בניהול → ספקי AI, הזינו מחדש את המפתחות הראשיים והגיבויים של כל מודל, בדקו את החיבורים ושמרו. ודאו שנבחר מודל תומך מסמכים לשירות התלושים ושהחילוץ מופעל בפרומפטים ותלושים. בדיקות חיבור וחילוץ עשויות לחייב את חשבון ה־AI.

ההגדרות ב־DB משותפות למחשב ולענן: החלפת הפניות המפתחות מהענן עלולה לגרום למחשב המקומי לאבד גישה אליהם. אין להזין ולשמור מפתחות לסירוגין משני שרתים בעלי keyring נפרד. לדמו מקומי נפרד, השתמשו בפרויקט Supabase נפרד או בתשתית ניהול סודות משותפת שהוכנה לכך.

אל תעתיקו את `.local/` או את כל קובצי המחשב ל־Git. תמונות הרכב והעובדים נשמרים במסד; תלוש מקורי אינו נשמר בקובצי השירות. ה־Volume נדרש למפתחות הצפנה ולהגדרות השרת, ויש לשמרו ולגבותו.

## בדיקת ההדגמה

1. התחברות מנהל; פתיחת קטלוג ותמונת רכב.
2. התחברות בחשבון עובד נפרד ובדיקת שיוך לפרופיל הנכון.
3. שאלה בצ׳אט והשוואת נטו.
4. העלאת תלוש סינתטי, השחרה, אישור, חילוץ ובדיקת השדות.
5. פתיחת פנייה לנציג, תשובה והמשך השיחה.
6. Redeploy בלי למחוק Volume; בדיקת התחברות, נתונים ושימוש במפתחות AI שכבר נשמרו.

ה־Hobby/תוכנית האירוח, המשאבים, הדיסק והתעבורה מחויבים לפי תנאי Railway בחשבון; קריאות AI מחויבות בנפרד. לא הופעלה כאן פריסה בתשלום או נוצר פרויקט Railway.

## תקלות נפוצות

- `נדרש חיבור HTTPS`: ודאו שנכנסים ב־HTTPS וש־`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` הוחל על השירות.
- Healthcheck נכשל: בדקו ב־Deploy Logs את שלב החיבור ל־Supabase, מפתח השרת, נתוני המקור והפורט. אם מוגדר AllowedHosts, עליו לכלול `healthcheck.railway.app`.
- מסך חיבור ראשוני/403 בהגדרה: הגדירו את Supabase ב־Variables וודאו שהטבלאות והנתונים קיימים. מסך ההגדרה המקומי אינו מסלול הגדרה מרחוק.
- אין מנהל: צרו את המנהל הראשון דרך `localhost` במחשב מול אותו Supabase, ואז התחברו בענן. אין לפתוח הרשאת bootstrap ציבורית.
- מפתח AI אינו ניתן לפענוח: הזינו מחדש בענן פעם אחת וודאו שה־Volume מחובר לפני השמירה. אם זה קרה אחרי Redeploy, בדקו שהדיסק והנתיבים לא הוחלפו.
- תקלת build בהורדת Microsoft/Node/NuGet: זו הורדה מתשתית הבנייה. בדקו את השורה שנכשלה ב־Build Logs; פקודת npm מקומית אינה מתקנת הורדה שנחסמה ב־Railway.

תיעוד Railway: [Dockerfiles](https://docs.railway.com/guides/dockerfiles), [Volumes](https://docs.railway.com/guides/volumes), [Public networking](https://docs.railway.com/guides/public-networking).

## היקף האימות של הכנת הפריסה

בניית תמונת Docker מלאה בסביבת Codex נחסמה בהורדת שכבת Microsoft גם לאחר ניסיון נוסף עם הרשאה. בניית חבילת הפרסום הצליחה; 23 בדיקות רלוונטיות עברו. נבדקו גם הגשת הממשק מתוך חבילת הפרסום, אתחול Production, זיהוי HTTPS מועבר, דחיית בקשות API רגילות ב־HTTP והעברת PORT וארגומנטים בפקודת ההפעלה. לא נבדקה פריסה חיה ב־Railway; יש לבצע את בדיקת ההדגמה לאחר יצירת השירות בחשבון.
