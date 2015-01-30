namespace Rebus
{
    /// <summary>
    /// Interface of something that is capable of receiving messages. If no message is available,
    /// null should be returned. If the bus is configured to run with multiple threads, this one
    /// should be reentrant.
    /// </summary>
    public interface IReceiveMessages : IReceiveMessageBase
    {
        /// <summary>
        /// Attempt to receive the next available message. Should return null if no message is available.
        /// </summary>
        ReceivedTransportMessage ReceiveMessage(ITransactionContext context);
    }
}