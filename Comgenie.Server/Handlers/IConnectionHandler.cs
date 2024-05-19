using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Handlers
{
    public interface IConnectionHandler
    {
        Task ClientConnect(Client client); // Client is connected
        Task ClientDisconnect(Client client); // Client is disconnected
        Task ClientReceiveData(Client client, byte[] buffer, int len); // Data received from the client
    }
}
