//TO DO
//Storage of mail
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Threading;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace EasyMailSMTP
{
    class Program
    {
        static void Main(string[] args)
        {
            var tcp = new TcpListener(IPAddress.Any, 25); //Listen to all addresses on port 25
            TcpClient tcphandler = default(TcpClient);
            tcp.Start();

            Console.WriteLine(">> SMTP server started (Indev)");

            while (true)
            {
                tcphandler = tcp.AcceptTcpClient();

                Console.WriteLine(">> New connection");
                handleClient client = new handleClient();
                client.startClient(tcphandler);
            }
        }
    }

    public class handleClient
    {
        Boolean debug = true; //Set to true to have a more verbose output to the console
            
        string smtpHostname = "localhost";
        string avaliableMailBox = "bart@localhost,root@localhost,postmaster@localhost"; //Just for testing

        string userMailBox = "";
        string messageData = "";
        string heloFrom = "";
        string mailFrom = "";

        Boolean currentlyHandlingData = false; //Mark true to accept following messages as DATA (Since they won't end with an \r and otherwise would get rejected)

        TcpClient clientSocket;
        NetworkStream networkStream;

        public void startClient(TcpClient inClientSocket)
        {
            this.clientSocket = inClientSocket;
            Thread t = new Thread(handleTCP);
            t.Start(); //Start new thread to handle the connection and return to normal operation
        }

        private void handleTCP()
        {
            byte[] bytesFrom = new byte[250];
            string dataFromClient = null;

            Boolean handeledSMTPHandshake = false;

            while ((clientSocket.Client.Connected))
            {
                try
                {
                    networkStream = clientSocket.GetStream();

                    if (handeledSMTPHandshake == false) { sendTCP("220 " + smtpHostname + " (EasyMail Indev)"); handeledSMTPHandshake = true; } //Only handshake once :)

                    if (networkStream.DataAvailable)
                    {
                        networkStream.Read(bytesFrom, 0, 250);
                        dataFromClient = System.Text.Encoding.UTF8.GetString(bytesFrom);
                        if (dataFromClient.Contains("\r") && currentlyHandlingData == false)
                        {
                            dataFromClient = dataFromClient.Substring(0, dataFromClient.IndexOf("\r"));
                            if (dataFromClient != "") { if (debug == true) { Console.WriteLine("|DEBUG|" + dataFromClient); } handleSMTP(dataFromClient); }
                        }
                        else if (currentlyHandlingData == true)
                        {
                            if (dataFromClient != "") { if (debug == true) { Console.WriteLine("|DEBUG|" + dataFromClient); } handleSMTP(dataFromClient); }
                        }
                    }
                    else { Thread.Sleep(1); }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(">> " + ex.ToString());
                    sendTCP("451 Unknown error"); //Maybe?
                }
            }
            Console.WriteLine(">> Connection closed");
        }

        private void handleSMTP(string dataFromClient)
        {
            Console.WriteLine(">> " + "Data received from client");

            if (currentlyHandlingData == true)
            {
                Boolean endOfData = false;
                string[] lines = Regex.Split(dataFromClient, "\r\n");
                dataFromClient = "";

                foreach (string line in lines)
                {
                    string finalString = line;
                    if (line.Length >= 1)
                    {

                        if (line.Substring(0, 1) == ".")
                        {
                            if (line.Length >= 2 && line.Substring(0, 2) == "..")//oh its just a normal dot, remove the first one to fix message.
                            {
                                finalString = finalString.Remove(0, 1);
                            }
                            else
                            {
                                endOfData = true;
                            }
                        }
                    }
                    dataFromClient += finalString + "\r\n";
                }

                
                if (endOfData)
                {
                    currentlyHandlingData = false;
                    userMailBox = "";
                    mailFrom = "";
                    sendTCP("250 Ok: Queued"); //This is a lie for now, it doesn't actually store messages - Will probably implement SQLite or custom format depending on if I feel like it. (Also I'm working on a IMAP server)
                }
            }
            //HELO (<hostname>)
            else if (dataFromClient.Length >= 4 && dataFromClient.Substring(0, 4) == "HELO")
            {
                if (dataFromClient.Length > 5)
                {
                    heloFrom = dataFromClient.Substring(4, (dataFromClient.Length - 4));
                    sendTCP("250 " + smtpHostname);
                }
                else { sendTCP("501 Syntax: HELO <hostname>"); } //Helo too short (No hostname)
            }
            //QUIT
            else if (dataFromClient.Length >= 4 && dataFromClient.Substring(0, 4) == "QUIT")
            {
                sendTCP("221 Bye");
                networkStream.Close(); //Close networkstream
            }
            //MAIL (From:<address>)
            else if (dataFromClient.Length >= 4 && dataFromClient.Substring(0, 4) == "MAIL")
            {
                if (dataFromClient.Length >= 11 && dataFromClient.ToLower().Contains("from:"))
                {
                    if (dataFromClient.Substring(5, 5).ToLower() == "from:")
                    {
                        mailFrom = dataFromClient.Substring(10, (dataFromClient.Length - 10)); //Get text after from:
                        mailFrom = mailFrom.Trim(' '); //Remove spaces
                        mailFrom = mailFrom.Trim('<'); //Should think of a better way to /properly/ implement this. For now just remove
                        mailFrom = mailFrom.Trim('>');
                        if (mailFrom.Length >= 1)
                        { //If there is still one character left, accept and Ok.
                            sendTCP("250 Ok");
                        }
                        else { sendTCP("501 Syntax: MAIL FROM:<address>"); } //After space removal, there is no address left
                    }
                    else { sendTCP("501 Syntax: MAIL FROM:<address>"); } //Malformed request?
                }
                else { sendTCP("501 Syntax: MAIL FROM:<address>"); } //Message too short and/or no from in message
            }
            // RCPT (To:<address>)
            else if (dataFromClient.Length >= 4 && dataFromClient.Substring(0, 4) == "RCPT")
            {
                if (mailFrom == "") //Need to set mail from first!!
                {
                    sendTCP("503 Need MAIL command first");
                }
                else if (dataFromClient.Length >= 9 && dataFromClient.ToLower().Contains("rcpt to:"))
                {
                    if (dataFromClient.Substring(5, 3).ToLower() == "to:")
                    {
                        string rcptMailBox = dataFromClient.Substring(8, (dataFromClient.Length - 8));
                        rcptMailBox = dataFromClient.Substring(8, (dataFromClient.Length - 8));
                        rcptMailBox = rcptMailBox.Trim(' ');
                        rcptMailBox = rcptMailBox.Trim('<'); //I'm not sure, but I think some email clients include these, Maybe its in the protocol specefications, I need to check on it.
                        rcptMailBox = rcptMailBox.Trim('>');
                        if (rcptMailBox.Length >= 1)
                        {
                            if (rcptAvaliable(rcptMailBox))
                            {
                                addToRcptList(rcptMailBox);
                                sendTCP("250 Ok");
                            }
                            else
                            {
                                sendTCP("550 <" + rcptMailBox + ">: Recipient address rejected: User unknown in local recipient table");
                            }
                        }
                        else { sendTCP("501 Syntax: RCPT TO:<address>"); } //RCPT was not followed by to: - Rejecting
                    }
                }
                else { sendTCP("501 Syntax: RCPT TO:<address>"); } //Message too short or no rcpt to: in message
            }
            else if (dataFromClient.Length >= 4 && dataFromClient.Substring(0, 4) == "DATA")
            {
                if (dataFromClient.Length > 4) //DATA shoudn't be longer then just "DATA"
                {
                    sendTCP("501 Syntax: DATA");
                }
                else
                {
                    if (userMailBox != "")
                    {
                        sendTCP("354 End data with <CRLF>.<CRLF> when done");
                        currentlyHandlingData = true; //Set to true to make sure new data actually gets threated as data and not as commands!
                    }
                    else
                    {
                        sendTCP("554 No (valid) recipients, please use RCPT command first");
                    }
                }
            }
            else if (dataFromClient.Length > 0) //After checking all other commands none of them matched, error.
            {
                sendTCP("502 Unknown command");
            }
        }

        private void sendTCP(string dataToSend)
        {
            Byte[] sendBytes = Encoding.UTF8.GetBytes(dataToSend + Environment.NewLine);
            networkStream.Write(sendBytes, 0, sendBytes.Length);
            networkStream.Flush();
            Console.WriteLine(">> " + dataToSend);
        }

        private void addToRcptList(string rcpt)
        {
            if (userMailBox == "") //If the mailbox is empty, just add the rcpt.
            {
                userMailBox = rcpt;
            }
            else
            {
                userMailBox += "," + rcpt;
            }
        }

        private Boolean rcptAvaliable(string rcpt)
        {
            //Check if mailbox is avaliable, if so return true
            if (avaliableMailBox.Contains(",")) //Check if our avaliableMailBox string contains more then one mailbox
            {
                string[] mailBoxes = Regex.Split(avaliableMailBox, ",");

                foreach (string mailBox in mailBoxes)
                {
                    if (mailBox == rcpt)
                    {
                        return true;
                    }
                }
                return false;
            }
            else
            {
                if (avaliableMailBox != "") //See if we have any mailboxes AT ALL
                {
                    if (avaliableMailBox == rcpt) //See if our only mailbox matches the current rcpt
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
        }
    }
}
