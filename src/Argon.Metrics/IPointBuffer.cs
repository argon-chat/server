namespace Argon.Metrics;

using InfluxDB3.Client.Write;

public interface IPointBuffer
{
    void Enqueue(PointData point);
}