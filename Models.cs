namespace WifiManager;

public class Plan
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public double Hours { get; set; }
    public double Price { get; set; }
}

public class Card
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string PlanName { get; set; } = "";
    public double Hours { get; set; }
    public double Price { get; set; }
    public bool Sold { get; set; }
    public string State => Sold ? "مباع" : "متاح";
    public string CreatedAt { get; set; } = "";
    public string SoldAt { get; set; } = "";
}

public class ActiveRow
{
    public string Id { get; set; } = "";
    public string User { get; set; } = "";
    public string Address { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string TimeLeft { get; set; } = "";
    public string Down { get; set; } = "";
    public string Up { get; set; } = "";
}

public class ReportRow
{
    public string Day { get; set; } = "";
    public long Count { get; set; }
    public double Total { get; set; }
}

public class LogRow
{
    public string Time { get; set; } = "";
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
}

public class LossRow
{
    public string Time { get; set; } = "";
    public double WanMB { get; set; }
    public double HotspotMB { get; set; }
    public double LossMB { get; set; }
    public double LossPct { get; set; }
}
