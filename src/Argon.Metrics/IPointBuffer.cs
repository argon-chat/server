namespace Argon.Metrics;

using InfluxDB.Client.Writes;

public interface IPointBuffer
{
    void Enqueue(PointData point);
}