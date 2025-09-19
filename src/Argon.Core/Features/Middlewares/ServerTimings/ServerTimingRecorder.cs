namespace Argon.Features.Middlewares;

public sealed class ServerTimingRecorder : IServerTimingRecorder
{
    private readonly List<Record> records = [];

    public IDisposable BeginRecord(string name, string? description = default)
    {
        var r = this.records.Where(a => a.Name == name && !a.IsFinished());
        if (r.Any())
            throw new ArgumentException("Recording is already started.", nameof(name));

        this.records.Add(new Record(name, description));
        return new TimingRecord(this, name);
    }

    public void EndRecord(string name)
    {
        var r = this.records.Where(a => a.Name == name && !a.IsFinished()).ToList();
        if (!r.Any())
            throw new ArgumentException("Recording does not exist.", nameof(name));

        r.Last().Finish();
    }

    public void EndRecord()
    {
        if (records.All(a => a.IsFinished()))
            throw new Exception("No started recordings exist.");
        records.Last().Finish();
    }

    public IReadOnlyList<Record> GetRecords() => records.AsReadOnly();

    public void Record(string name, double? duration = default, string? description = default) =>
        records.Add(new Record(name, description, duration));
}