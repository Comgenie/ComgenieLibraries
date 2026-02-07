using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers.Dns
{
    /// <summary>
    /// Placeholder for future DNS handler
    /// </summary>
    public class DnsHandler : IConnectionHandler
    {
        public Task ClientConnectAsync(Client client, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task ClientDisconnectAsync(Client client, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task ClientReceiveDataAsync(Client client, byte[] buffer, int len, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
