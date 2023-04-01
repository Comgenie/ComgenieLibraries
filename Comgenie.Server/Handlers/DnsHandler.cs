using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers
{
    public class DnsHandler : IConnectionHandler
    {
        public void ClientConnect(Client client)
        {
            throw new NotImplementedException();
        }

        public void ClientDisconnect(Client client)
        {
            throw new NotImplementedException();
        }

        public void ClientReceiveData(Client client, byte[] buffer, int len)
        {
            throw new NotImplementedException();
        }
    }
}
