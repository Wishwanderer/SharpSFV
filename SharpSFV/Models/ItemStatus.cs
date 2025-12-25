namespace SharpSFV
{
    public enum ItemStatus : byte
    {
        Queued,
        Pending, // Processing
        OK,
        Bad,
        Missing,
        Error
    }
}