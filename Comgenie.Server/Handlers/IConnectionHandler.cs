using System;
using System.Collections.Generic;
using System.Text;

namespace Comgenie.Server.Handlers
{
    public interface IConnectionHandler
    {
        void ClientConnect(Client client); // Client is connected
        void ClientDisconnect(Client client); // Client is disconnected
        void ClientReceiveData(Client client, byte[] buffer, int len); // Data received from the client
    }
}
