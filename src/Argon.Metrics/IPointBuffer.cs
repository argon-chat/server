namespace Argon.Metrics;

using InfluxDB.Client.Writes;

public interface IPointBuffer
{
    /// <summary>
/// Adds a PointData object to the buffer for later processing or transmission.
/// </summary>
/// <param name="point">The data point to enqueue.</param>
void Enqueue(PointData point);
}