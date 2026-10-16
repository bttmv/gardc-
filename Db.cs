using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WifiManager;

public static class Db
{
    static string _path = "";

    static SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_path}");
        c.Open();
        return c;
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    static void Exec(string sql, params (string, object?)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    static string Str(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? "" : r.GetString(i);
    }

    public static void Init(string path)
    {
        _path = path;
        Exec(@"
CREATE TABLE IF NOT EXISTS plans(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, hours REAL NOT NULL, price REAL NOT NULL);
CREATE TABLE IF NOT EXISTS cards(id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT UNIQUE NOT NULL, plan_name TEXT NOT NULL,
  hours REAL NOT NULL, price REAL NOT NULL, sold INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, sold_at TEXT);
CREATE TABLE IF NOT EXISTS oplog(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, action TEXT NOT NULL, details TEXT);
CREATE TABLE IF NOT EXISTS traffic(id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, wan_total INTEGER NOT NULL, hs_total INTEGER NOT NULL);");
        if (Query("SELECT COUNT(*) AS n FROM plans", r => r.GetInt64(0)).First() == 0)
        {
            AddPlan("ساعة", 1, 500);
            AddPlan("3 ساعات", 3, 1200);
            AddPlan("يوم كامل (24 ساعة)", 24, 3000);
        }
    }

    // ---- الباقات ----
    public static List<Plan> Plans() => Query("SELECT * FROM plans ORDER BY hours",
        r => new Plan { Id = r.GetInt64(r.GetOrdinal("id")), Name = Str(r, "name"), Hours = r.GetDouble(r.GetOrdinal("hours")), Price = r.GetDouble(r.GetOrdinal("price")) });
    public static void AddPlan(string name, double hours, double price) =>
        Exec("INSERT INTO plans(name,hours,price) VALUES($n,$h,$p)", ("$n", name), ("$h", hours), ("$p", price));
    public static void DeletePlan(long id) => Exec("DELETE FROM plans WHERE id=$i", ("$i", id));

    // ---- الكروت ----
    public static void AddCard(string code, string plan, double hours, double price) =>
        Exec("INSERT INTO cards(code,plan_name,hours,price,created_at) VALUES($c,$pl,$h,$pr,$t)",
            ("$c", code), ("$pl", plan), ("$h", hours), ("$pr", price), ("$t", Now()));
    public static bool CodeExists(string code) =>
        Query("SELECT 1 FROM cards WHERE code=$c", r => 1, ("$c", code)).Count > 0;
    public static List<Card> Cards(bool onlyUnsold = false) =>
        Query("SELECT * FROM cards" + (onlyUnsold ? " WHERE sold=0" : "") + " ORDER BY id DESC",
            r => new Card
            {
                Id = r.GetInt64(r.GetOrdinal("id")), Code = Str(r, "code"), PlanName = Str(r, "plan_name"),
                Hours = r.GetDouble(r.GetOrdinal("hours")), Price = r.GetDouble(r.GetOrdinal("price")),
                Sold = r.GetInt64(r.GetOrdinal("sold")) == 1, CreatedAt = Str(r, "created_at"), SoldAt = Str(r, "sold_at")
            });
    public static void MarkSold(string code) =>
        Exec("UPDATE cards SET sold=1, sold_at=$t WHERE code=$c AND sold=0", ("$t", Now()), ("$c", code));
    public static void DeleteCard(string code) => Exec("DELETE FROM cards WHERE code=$c", ("$c", code));

    // ---- التقارير ----
    public static List<ReportRow> ReportByDay() => Query(
        "SELECT substr(sold_at,1,10) AS day, COUNT(*) AS n, SUM(price) AS total FROM cards WHERE sold=1 GROUP BY day ORDER BY day DESC",
        r => new ReportRow { Day = Str(r, "day"), Count = r.GetInt64(r.GetOrdinal("n")), Total = r.GetDouble(r.GetOrdinal("total")) });

    public static (long n, double sum) SoldTotals(string prefix)
    {
        var rows = Query(
            "SELECT COUNT(*) AS n, COALESCE(SUM(price),0) AS s FROM cards WHERE sold=1 AND sold_at LIKE $p",
            r => (r.GetInt64(0), r.GetDouble(1)), ("$p", prefix + "%"));
        return rows[0];
    }

    // ---- سجل العمليات ----
    public static void Log(string action, string details = "") =>
        Exec("INSERT INTO oplog(ts,action,details) VALUES($t,$a,$d)", ("$t", Now()), ("$a", action), ("$d", details));
    public static List<LogRow> LogRows(int limit = 500) => Query(
        "SELECT * FROM oplog ORDER BY id DESC LIMIT " + limit,
        r => new LogRow { Time = Str(r, "ts"), Action = Str(r, "action"), Details = Str(r, "details") });

    // ---- قياس الفاقد ----
    public static void AddTraffic(long wan, long hs) =>
        Exec("INSERT INTO traffic(ts,wan_total,hs_total) VALUES($t,$w,$h)", ("$t", Now()), ("$w", wan), ("$h", hs));
    public static List<(string ts, long wan, long hs)> Traffic(int limit = 600) => Query(
        "SELECT * FROM (SELECT * FROM traffic ORDER BY id DESC LIMIT " + limit + ") ORDER BY id ASC",
        r => (Str(r, "ts"), r.GetInt64(r.GetOrdinal("wan_total")), r.GetInt64(r.GetOrdinal("hs_total"))));
}
