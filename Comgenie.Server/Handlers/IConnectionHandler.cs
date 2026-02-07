using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers
{
    /// <summary>
    /// Interface for connection handlers. 
    /// Connection handlers are responsible for handling all incoming data and sending response data to the clients.
    /// </summary>
    public interface IConnectionHandler
    {
        /// <summary>
        /// Executed when a new client is connected and handed to this handler
        /// </summary>
        /// <param name="client">Newly connected client</param>
        /// <param name="cancellationToken">Cancellation token attached to this client connection</param>
        /// <returns>Task</returns>
        Task ClientConnectAsync(Client client, CancellationToken cancellationToken);

        /// <summary>
        /// Executed when a client is disconnecting or has been disconnected. The connection is usually closed at this point.
        /// Use this to clean up any data loaded for this client in this handler.
        /// </summary>
        /// <param name="client">Disconnected client</param>
        /// <param name="cancellationToken">Cancellation token attached to this client connection</param>
        /// <returns>Task</returns>
        Task ClientDisconnectAsync(Client client, CancellationToken cancellationToken);

        /// <summary>
        /// Executed when data is received from this client. This is after decrypting it from the TLS stream.
        /// Note that data is usually received partially and almost never all at once. 
        /// </summary>
        /// <param name="client">Client that the data is retrieved from</param>
        /// <param name="buffer">Buffer containing the data, note that this buffer is often bigger than the actual received data</param>
        /// <param name="len">Length of the data within the buffer actually received</param>
        /// <param name="cancellationToken">Cancellation token attached to this client connection</param>
        /// <returns>Task</returns>
        Task ClientReceiveDataAsync(Client client, byte[] buffer, int len, CancellationToken cancellationToken); // Data received from the client
    }
}
