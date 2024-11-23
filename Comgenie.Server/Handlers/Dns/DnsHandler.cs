using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers.Dns
{
    /// <summary>
    /// Placeholder for future DNS handler
    /// </summary>
    public class DnsHandler : IConnectionHandler
    {
        public Task ClientConnect(Client client)
        {
            throw new NotImplementedException();
        }

        public Task ClientDisconnect(Client client)
        {
            throw new NotImplementedException();
        }

        public Task ClientReceiveData(Client client, byte[] buffer, int len)
        {
            throw new NotImplementedException();
        }
    }
}
