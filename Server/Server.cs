using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using System.Timers;

namespace Server
{
    class Server
    {
        #region Constants
        private readonly int PORT = 50007;
        private readonly uint RECEIVED_MESSAGE_SIZE = 16;
        private readonly uint START_STREAM_CMD = 0xA0A0A0A0;
        private readonly uint TPSN_SYNC_WORD = 0xABABABAB;
        private readonly uint ALIGNMENT_WORD = 0xA5A5A5A5;
        private readonly uint WAIT_BEFORE_START_STREAM_MS = 10000; // milliseconds.
        #endregion

        #region Variables
        private TcpListener _listener;
        private Thread _listenerThread;
        private List<TcpClient> _clientList;
        private readonly string[] _logFileNames = { @"C:\Users\Nastase\Desktop\dev0.log", @"C:\Users\Nastase\Desktop\dev1.log" };
        private static System.Timers.Timer _timer;
        private readonly DateTime _startDate;
        #endregion

        /// <summary>
        /// The constructor does some cleanup and starts the listener thread.
        /// </summary>
        public Server()
        {
            foreach (string s in _logFileNames)
            {
                if (File.Exists(s))
                {
                    File.Delete(s);
                }
            }

            _startDate = DateTime.Today;

            _listener = new TcpListener(System.Net.IPAddress.Any, PORT);
            Console.WriteLine($"Server running at port {PORT}...\n");
            _listenerThread = new Thread(new ThreadStart(ManageClients));
            _listenerThread.Start();
        }

        /// <summary>
        /// Listens for new connections and creates a new thread for each client that connects.
        /// </summary>
        private void ManageClients()
        {
            _listener.Start();
            _clientList = new List<TcpClient>();
            uint clientID = 0;

            while (true)
            {
                TcpClient client = _listener.AcceptTcpClient();
                _clientList.Add(client);
                clientID++;

                if (clientID == 2)
                {
                    SetTimer();
                }

                Console.WriteLine($"### New client from address {client.Client.RemoteEndPoint}; ID = {clientID - 1} ###");

                // Create and start the client thread.
                Thread thread = new Thread(delegate ()
                {
                    ClientHandler(client, clientID - 1);
                });
                thread.Start();
            }
        }

        /// <summary>
        /// Handles the connection for the given client.
        /// </summary>
        /// <param name="client">The current client instance.</param>
        /// <param name="clientID">The uniqued ID assigned to the current client.</param>
        void ClientHandler(object client, uint clientID)
        {
            TcpClient tcpClient = (TcpClient)client;
            NetworkStream clientStream = tcpClient.GetStream();

            // Incoming message buffer.
            byte[] messageFromClient = new byte[RECEIVED_MESSAGE_SIZE];
            // The numbers of bytes read from the connection.
            int bytesRead = 0;

            while (true)
            {
                bytesRead = 0;
                Array.Clear(messageFromClient, 0, messageFromClient.Length);

                try
                {
                    // Read bytes from stream.
                    bytesRead = clientStream.Read(messageFromClient, 0, (int)RECEIVED_MESSAGE_SIZE);
                    // Get the receive time to be used for TPSN synchronization.
                    long receiveTime = GetCurrentTimeMicrosec();
                    ParseReceivedMessage(messageFromClient, bytesRead, clientStream, clientID, receiveTime);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ClientHandler: " + ex.Message);
                    break;
                }
            }

            // Client disconnected.
            Console.WriteLine("### Client with ip " + tcpClient.Client.RemoteEndPoint.ToString() + " disconnected from server ###");
            _clientList.Remove(tcpClient);
            tcpClient.Close();
        }

        /// <summary>
        /// Sends a message to a TCP client.
        /// </summary>
        /// <param name="message">The message to be sent.</param>
        /// <param name="ns">The client's network stream.</param>
        void SendMessageToClient(byte[] message, NetworkStream ns)
        {
            ns.Write(message, 0, message.Length);
        }

        /// <summary>
        /// Broadcast a message to all clients.
        /// </summary>
        /// <param name="message">The message to be broadcasted.</param>
        void BroadcastMessage(byte[] message)
        {
            NetworkStream ns;

            foreach (TcpClient cl in _clientList)
            {
                ns = cl.GetStream();
                SendMessageToClient(message, ns);
            }
        }

        /// <summary>
        /// Processes the received message from a client.
        /// </summary>
        /// <param name="messageBytes">The received message.</param>
        /// <param name="bytesRead">The number of bytes in the received message.</param>
        /// <param name="ns">The client's network stream.</param>
        /// <param name="clientID">The client ID.</param>
        /// <param name="receiveTime">The time that the message was received on, expressed in microseconds.</param>
        void ParseReceivedMessage(byte[] messageBytes, int bytesRead, NetworkStream ns, uint clientID, long receiveTime)
        {
            try
            {
                // Write the raw data to file.
                WriteToFile(clientID, messageBytes, bytesRead);

                // Convert the byte array to an uint array.
                uint[] message = new uint[messageBytes.Length / 4];
                Buffer.BlockCopy(messageBytes, 0, message, 0, messageBytes.Length);

                // Get the synchronizaion word.
                uint sync_word = message[0];

                // Do TPSN synchronization.
                if (sync_word == TPSN_SYNC_WORD && bytesRead >= 12)
                {
                    // Get convert the receive time to microseconds and seconds.
                    uint receiveSec = (uint)(receiveTime / 1000000);
                    uint receiveMicr = (uint)(receiveTime % 1000000);
                    // Get the current time and convert to seconds and microseconds.
                    long sendTime = GetCurrentTimeMicrosec();
                    uint sendSec = (uint)(sendTime / 1000000);
                    uint sendMicr = (uint)(sendTime % 1000000);
                    // Assemble the message.
                    uint[] msg = new uint[] { TPSN_SYNC_WORD, message[1], message[2], receiveSec, receiveMicr, sendSec, sendMicr };
                    // Convert to byte array.
                    byte[] msgBytes = msg.SelectMany(BitConverter.GetBytes).ToArray();
                    // Send the message.
                    SendMessageToClient(msgBytes, ns);

                    Console.WriteLine($"TPSN sync for client {clientID} done at {DateTime.Now.TimeOfDay}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ParseReceivedMessage: " + ex.Message);
                return;
            }
        }

        /// <summary>
        /// Writes data to the current client's log file.
        /// </summary>
        /// <param name="clientID">The client ID.</param>
        /// <param name="data">Data to be written.</param>
        /// <param name="bytesRead">The number of bytes in the data array.</param>
        private void WriteToFile(uint clientID, byte[] data, int bytesRead)
        {
            string filename = _logFileNames[clientID];

            using (var fileStream = new FileStream(filename, FileMode.Append, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fileStream))
            {
                bw.Write(data, 0, bytesRead);
            }
        }
        
        /// <summary>
        /// Sets up the timer that will send the START_STREAM command to the connected devices (clients).
        /// </summary>
        private void SetTimer()
        {
            // Set the wait interval.
            _timer = new System.Timers.Timer(WAIT_BEFORE_START_STREAM_MS);
            _timer.Elapsed += OnTimerExpired;
            _timer.AutoReset = false;
            _timer.Enabled = true;
        }

        /// <summary>
        /// The handler of the "timer expired" event. Sends the START_STREAM command.
        /// </summary>
        /// <param name="sender">The object that raised the event.</param>
        /// <param name="e">Event argument.</param>
        private void OnTimerExpired(object sender, ElapsedEventArgs e)
        {
            // Assemble the message.
            uint[] msg = { ALIGNMENT_WORD, START_STREAM_CMD };
            // Convert to byte array.
            byte[] msgBytes = msg.SelectMany(BitConverter.GetBytes).ToArray();

            BroadcastMessage(msgBytes);
            Console.WriteLine("");
            Console.WriteLine("---- Data stream started! ----");
            Console.WriteLine("");
        }

        /// <summary>
        /// Gets the time difference, in microseconds, from a fixed date and the current time.
        /// </summary>
        /// <returns>The time difference, in microseconds.</returns>
        private long GetCurrentTimeMicrosec()
        {
            long elapsedMicrosec = 0;
            DateTime now = DateTime.Now;

            TimeSpan difference = now.Subtract(_startDate);
            elapsedMicrosec = difference.Ticks / 10;

            return elapsedMicrosec;
        }


    }
}
