namespace PrintQueue.Api.Domain;

public class Printer
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string Location { get; private set; } = string.Empty;
    public bool IsOnline { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }

    public ICollection<PrintJob> Jobs { get; private set; } = new List<PrintJob>();

    private Printer()
    {
    }

    public static Printer Create(string name, string location, DateTime utcNow)
    {
        return new Printer
        {
            Name = name.Trim(),
            Location = location.Trim(),
            IsOnline = true,
            CreatedAt = utcNow
        };
    }

    public void SetOnline(bool isOnline)
    {
        IsOnline = isOnline;
    }
}
