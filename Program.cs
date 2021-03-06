﻿//Useful: http://www.samlogic.net/articles/smtp-commands-reference.htm
//TO DO
//Storage of mail
//Email forwarding for non local addresses
//Support for mailing lists? (Also EXPN command)
//Enable switch for allowing relay, make VRFY awnser with 554 if relay access is disabled (251 if accepted / will attempt), deny RCPT TO: etc. 
//Extend possible EHLO attributes(?)
//Properly work with MAIL FROM:< > AUTH=


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
using System.Security.Cryptography;

namespace EasyMailSMTP
{
    class Program
    { 
        static void Main(string[] args)
        {
            var tcp = new TcpListener(IPAddress.Any, 25); //Listen to all addresses on port 25
            TcpClient tcphandler = default(TcpClient);
            try
            {
                tcp.Start();
            }
            catch{
                Console.WriteLine(">> Coudn't open TCP port");
                Console.WriteLine(">> (Is the port not in use and do we have the right permissions?)");
                Environment.Exit(1);
            }

            Console.WriteLine(">> SMTP server started (Indev)");

            titleUpdate titleUpdater = new titleUpdate();
            titleUpdater.Start();

            while (true)
            {
                tcphandler = tcp.AcceptTcpClient();
                Console.WriteLine(">> New connection");
                handleClient client = new handleClient();
                client.startClient(tcphandler, titleUpdater);
            }
        }
    }

    public class titleUpdate
    {
        System.Timers.Timer updateTitle = new System.Timers.Timer();
        
        int clientsConnected = 0;
        int clientsTotal = 0;
        int messagesSent = 0;

        Boolean running = false;
        Boolean initiated = false;

        public void Start()
        {
            if (!initiated)
            {
                updateTitle.Elapsed += new System.Timers.ElapsedEventHandler(updateTitleEvent);
                updateTitle.Interval = 1000; //Run every 1000ms --> 1 second
                initiated = true;
            }
            if (!running)
            {
                running = true;
                updateTitle.Start();
            }
        }

        public void Stop()
        {
            if (running)
            {
                running = false;
                updateTitle.Stop();
            }
        }

        public void Update(int connected, int total, int messages)
        {
            clientsConnected = connected;
            clientsTotal = total;
            messagesSent = messages;
        }

        public int ClientConnect()
        {
            clientsConnected++;
            clientsTotal++;
            return clientsTotal;
        }

        public void ClientDisconnected()
        {
            clientsConnected--;
        }

        public void MessageSent()
        {
            messagesSent++;
        }

        public int Connected()
        {
            return clientsConnected;
        }

        public int Total()
        {
            return clientsTotal;
        }

        public int Messages()
        {
            return messagesSent;
        }

        private void updateTitleEvent(object sender, System.Timers.ElapsedEventArgs e)
        {
            Console.Title = "EasyMail SMTP (Indev) - Connected: " + clientsConnected + "/" + clientsTotal + " - Messages sent: " + messagesSent;
        }
    }

    public class handleClient
    {
        int bufferSize = 8 * 1024 * 64; //64KB buffer, should probably be increased. Seems to have speed increase at receiving of DATA
        Boolean debug = true; //Set to true to have a more verbose output to the console
            
        string smtpHostname = "localhost"; //Hostname of this SMTP server
        string avaliableMailBox = "bart@localhost,root@localhost,postmaster@localhost"; //Just for testing (Will replace with proper database / storage solution later)

        string credentials = "bart:test123"; //SMTP creditials (Should be replaced with better solution later)
        Boolean userAuthenticated = false; //true if the client has completed successful authentication
        string userAuthenticatedAs = ""; //The current client has authenticated as this user
        Boolean currentlyAuthenticating = false;
        Boolean requireAuthentication = false; //CURRENTLY NOT FINISHED YET!! DO NOT SET TO TRUE //Set to false to allow usage without authentication - true to require authentication

        string storageMode = "file";

        int connectionID = 0;

        string userMailBox = ""; //The recipient mailbox(es)
        string messageData = ""; //The message's DATA
        string heloFrom = ""; //Name that was entered during HELO command
        string mailFrom = ""; //Address from MAIL FROM:<address> command
        string bodytype = ""; //Bodytype if specefic (Assume 8-bit always)
        int messagesizeExpected = -1; //-1 = Not in use

        Boolean messageDataEmpty = true;
        Boolean currentlyHandlingData = false; //Mark true to accept following messages as DATA (Since they won't end with an \r and otherwise would get rejected)

        TcpClient clientSocket;
        NetworkStream networkStream;

        titleUpdate titleUpdater;

        int dataSize = 0; //Semi accurate Size counter by increasing by *buffersize* on every run (Does also count null characters at the moment)
        MemoryStream dataStream; //To use while processing the DATA being received
        StreamWriter dataStreamWriter; //Streamwriter used to write to the memorystream
        StreamReader dataStreamReader; //Streamreader used to read the memorystream

        //Connection limits as by RFC2821
        int domainLengthMax = 255; //Maximum domainname size (characters after @)
        int commandlineMax = 512; //Maximum length of commandline, so the whole line (including command word and CRLF)
        int maxDataSize = 20; //Value in MB, gets convered to bytes on load
        int recipientsMax = 1000; //Should accept minimum of 100 as by RFC2821. No maximum listed.

        //Connection timeouts following RFC2821
        System.Timers.Timer timeoutTimer;
        int timeoutDATAInit = 60 * 4; //Minimum 2 minutes - we set 4 (Timeout before DATA starts coming in after sending a 354 status code to the client)
        int timeoutDATABlock = 60 * 3; //Minimum 3 minutes - we set 3 (Because having 3 minutes between TCP SEND calls is already insane, Timeout between DATA Chunks/Blocks)
        int timeout = 60 * 12; //Minimum 5 minutes - we set 12 (Timeout between commands)

        public void startClient(TcpClient inClientSocket, titleUpdate _titleUpdater)
        {
            this.clientSocket = inClientSocket;
            titleUpdater = _titleUpdater;

            maxDataSize = 1024 * 1024 * maxDataSize; //Maximum of 20MB emails
            //Convert limits to miliseconds!
            timeoutDATAInit = timeoutDATAInit * 1000;
            timeoutDATABlock = timeoutDATABlock * 1000;
            timeout = timeout * 1000;

            timeoutTimer = new System.Timers.Timer();

            Thread t = new Thread(handleTCP);
            t.Start(); //Start new thread to handle the connection and return to normal operation
        }

        private void handleTCP()
        {
            connectionID = titleUpdater.ClientConnect();

            byte[] bytesFrom = new byte[bufferSize];
            string dataFromClient = null;

            Boolean handeledSMTPHandshake = false;
            networkStream = clientSocket.GetStream();

            timeoutTimer.Interval = timeout;
            timeoutTimer.Elapsed += new System.Timers.ElapsedEventHandler(connectionTimeoutHandle);
            timeoutTimer.Start();

            while (clientSocket.Client.Connected)
            {
                try
                {

                    if (handeledSMTPHandshake == false) { sendTCP("220 " + smtpHostname + " (EasyMail Indev)"); handeledSMTPHandshake = true; } //Only handshake once :)

                    if (networkStream.DataAvailable)
                    {
                        networkStream.Read(bytesFrom, 0, bufferSize);
                        dataFromClient = System.Text.Encoding.UTF8.GetString(bytesFrom); //Convert the byte array to a UTF8 string
                        bytesFrom = new byte[bufferSize]; //Clear byte array to erase previous messages

                        if (dataFromClient.Contains("\r\n") && currentlyHandlingData == false) //Only process data if it contains a newline and is not a DATA package
                        {
                            dataFromClient = dataFromClient.Substring(0, dataFromClient.IndexOf("\r\n"));
                            if (dataFromClient != "") { if (debug) { Console.WriteLine("|DEBUG|" + dataFromClient); } handleSMTP(dataFromClient); }
                        }
                        else if (currentlyHandlingData) //Process because it should be DATA
                        {
                            if (dataFromClient != "") { if (debug) { Console.WriteLine("|DEBUG|" + dataFromClient); } handleSMTP(dataFromClient); }
                        }
                    }
                    else { Thread.Sleep(1); } //Sleep for 1ms to prevent a high speed loop using all the processing power
                }
                catch (Exception ex)
                {
                    Console.WriteLine(">> " + ex.ToString());
                    sendTCP("451 Unknown error"); //Internal error while processing, should report back to client or the command will simply time out
                }
            }
            if (currentlyHandlingData)
            {
                dataStream.Close();
                dataStream.Dispose();
            }
            timeoutTimer.Dispose();
            networkStream.Dispose();
            titleUpdater.ClientDisconnected();
            Console.WriteLine(">> Connection closed");
        }

        private void connectionTimeoutHandle(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                sendTCP("221 Connection timeout - Closing");
                if (currentlyHandlingData)
                {
                    dataStream.Close();
                    dataStream.Dispose();
                }
                timeoutTimer.Dispose();
                networkStream.Close();
                networkStream.Dispose();
                Console.WriteLine("[T] Connection should be closing / cleared");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[T] " + ex.ToString());
            }
        }

        private void handleSMTP(string dataFromClient)
        {
            if (debug) { Console.WriteLine(">> " + "Data received from client"); }

            if (currentlyHandlingData) //We're supposed to be handeling DATA, do not check for commands
            {
                timeoutTimer.Interval = timeoutDATABlock; //Reset the timer to 0 again since we received a datablock
                Boolean endOfData = false;
                string[] lines = Regex.Split(dataFromClient.Replace("\0", ""), "\r\n"); //Split every newline and remove zero characters (\0)

                foreach (string line in lines)
                {
                    string finalString = line;
                    if (line.Length >= 1 && endOfData == false)
                    {
                        if (line.Substring(0, 1) == ".") //Check if the first character is a dot
                        {
                            if (line.Length >= 2 && line.Substring(0, 2) == "..")//After first dot another dot follows, the first two dots should be escapted to just one dot. (Its NOT end of DATA!)
                            {
                                finalString = finalString.Remove(0, 1); //Remove first character (escape dot)
                            }
                            else if (line.Length == 1) //Only if there is no next character!
                            {
                                finalString = ""; //Empty the string, we don't need to interpent the endOfData dot as a part of the message
                                endOfData = true;
                            }
                        }
                    }
                    if (endOfData == false) //Only process if it has not reached the end of data dot. (Without it also starts processing additional headers from the mail clients connection or null characters)
                    {
                        if (!messageDataEmpty) { dataStreamWriter.Write("\r\n"); } //Add newline since there is already data from previous DATA packages
                        dataStreamWriter.Write(finalString); //Add the line to the main DATA string
                        messageDataEmpty = false;
                    }
                }

                dataSize = dataSize + (bufferSize / 8);

                if (dataSize > maxDataSize) //While sending the message the maximum allowed size was reached! Most clients handle sudden interuptions like this horribly, but keeping on accepting the data isn't good either!
                {
                    sendTCP("552 Email bigger then maximum allowed (" + maxDataSize + "Bytes)");

                    currentlyHandlingData = false;
                    userMailBox = "";
                    mailFrom = "";
                    messageData = "";
                    messageDataEmpty = true;
                    dataSize = 0;
                    dataStreamWriter.Close();
                    dataStreamReader.Close();
                    dataStream.Close();
                    
                    timeoutTimer.Interval = timeout;
                }else if (endOfData) //If the end of data dot was received, send message and clear variables
                {
                    dataStreamWriter.Flush();
                    dataStream.Seek(0, SeekOrigin.Begin);
                    messageData = dataStreamReader.ReadToEnd();

                    if (debug) { Console.WriteLine("|DEBUG| Final DATA output:" + Environment.NewLine + messageData); }
                    currentlyHandlingData = false;
                    Boolean mailsuccess = storeMessage(messageData, connectionID);
                    userMailBox = "";
                    mailFrom = "";
                    messageData = "";
                    messageDataEmpty = true;
                    dataSize = 0;
                    dataStreamWriter.Close();
                    dataStreamReader.Close();
                    dataStream.Close();
                    if (mailsuccess)
                    {
                        sendTCP("250 Ok: Queued");
                    }
                    else
                    {
                        sendTCP("451 Error while processing mail delivery, try again at a later moment");
                    }
                    titleUpdater.MessageSent();
                    timeoutTimer.Interval = timeout;
                }
            }
            else if (currentlyAuthenticating) //Handle authentication
            {
                try
                {
                    string authBase64 = Encoding.UTF8.GetString(Convert.FromBase64String(dataFromClient));

                    string[] authBase64Split = authBase64.Split('\0');

                    Thread.Sleep(300); //Sleep for 300ms, this makes it harder for bruteforcing to be profitable.

                    if (authBase64Split.Length == 3 && (authBase64Split[1] + ":" + authBase64Split[2]) == credentials)
                    {
                        currentlyAuthenticating = false;
                        userAuthenticated = true;
                        userAuthenticatedAs = authBase64Split[1];
                        sendTCP("235 Authentication successful, welcome " + authBase64Split[1]);
                    }
                    else
                    {
                        currentlyAuthenticating = false;
                        sendTCP("535 Authentication failed");
                    }
                }
                catch
                {
                    currentlyAuthenticating = false;
                    sendTCP("535 Authentication failed");
                }
            }
            else if (dataFromClient.Length >= 4 && (dataFromClient.Length + 4) <= commandlineMax) //Add 4 characters to the length when checking command line size since the \r\n got stripped (4 characters)
            {
                timeoutTimer.Interval = timeout; //Reset the timer to 0 again since we received a command

                //HELO (<hostname>)
                if (dataFromClient.Substring(0, 4) == "HELO")
                {
                    if (dataFromClient.Length > 5)
                    {
                        string helo = dataFromClient.Substring(5, dataFromClient.Length - 5).Replace(" ", ""); //Get helo contents and remove spaces
                        if (helo.Length >= 1)
                        {
                            heloFrom = helo;
                            sendTCP("250 " + smtpHostname + " - Welcome " + heloFrom + "!"); //Reply back with status code 250 and repeat our hostname and the received HELO
                        }
                        else
                        {
                            sendTCP("501 Syntax: HELO <hostname>"); //While HELO was received, no characters were left after
                        }
                    }
                    else { sendTCP("501 Syntax: HELO <hostname>"); } //Helo too short (No hostname)
                }
                //EHLO <hostname>
                else if (dataFromClient.Substring(0, 4).ToUpper() == "EHLO") //Extended SMTP
                {
                    if (dataFromClient.Length > 5)
                    {
                        string ehlo = dataFromClient.Substring(5, dataFromClient.Length - 5).Replace(" ", ""); //Get ehlo contents and remove spaces
                        if (ehlo.Length >= 1)
                        {
                            heloFrom = ehlo;
                            sendTCP("250-" + smtpHostname + " - Welcome " + heloFrom + "!"); //Reply back with status code 250 and repeat our hostname and the received EHLO
                            sendTCP("250-SIZE " + maxDataSize); //Announce max message size
                            sendTCP("250-8BITMIME"); //Announce we operate under 8-bit
                            sendTCP("250-AUTH PLAIN"); //JUST FOR TESTING AT THE MOMENT!
                            sendTCP("250 HELP"); //Announce we support the HELP command
                        }
                        else
                        {
                            sendTCP("501 Syntax: EHLO <hostname>"); //While EHLO was received, no characters were left after
                        }
                    }
                    else { sendTCP("501 Syntax: EHLO <hostname>"); } //Ehlo too short (No hostname)
                }
                //QUIT
                else if (dataFromClient.Substring(0, 4).ToUpper() == "QUIT")
                {
                    sendTCP("221 Bye"); //Servers SHOULD reply with a 221 status code, but its not explicitly needed. Its nice to do so though.
                    networkStream.Close(); //Close networkstream
                }
                //MAIL (From:<address>)
                else if (dataFromClient.Substring(0, 4).ToUpper() == "MAIL")
                {
                    if (heloFrom == "")
                    {
                        sendTCP("503 Please send HELO/EHLO first");
                    }
                    else
                    {
                        if (dataFromClient.Length >= 11 && dataFromClient.ToUpper().Contains("FROM:")) //Make sure the MAIL command actually includes a from:
                        {
                            if (dataFromClient.Substring(5, 5).ToUpper() == "FROM:") //Make sure the from: is in the right position as expected
                            {
                                if (dataFromClient.ToUpper().Contains("AUTH=")) //AUTH= Parameter specified, try to authenticate before proceeding
                                {
                                    try
                                    {
                                        string authData = dataFromClient.Remove(0, dataFromClient.Replace("auth=", "AUTH=").LastIndexOf("AUTH=") + 5);
                                        if (authData.Contains(" "))
                                        {
                                            authData = authData.Remove(authData.IndexOf(" ")); //Remove everything after the space as it might be another command we don't need to process with AUTH=

                                            //Handle actual authentication
                                        }
                                    }
                                    catch
                                    {
                                        sendTCP("535 5.7.8  Authentication credentials invalid");
                                        userAuthenticated = false;
                                        userAuthenticatedAs = "";
                                    }
                                }

                                Boolean userAuthenticationAccepted = true;
                                if (requireAuthentication)
                                {
                                    userAuthenticationAccepted = userAuthenticated; //Set userAuthenticationAccepted to the value if there has been authenticated (userAthenticated returns true if authentication succeeded)
                                }

                                if (userAuthenticationAccepted)
                                {
                                    long messagesizeLong = 0;
                                    int messagesize = 0;

                                    mailFrom = dataFromClient.Substring(10, (dataFromClient.Length - 10)); //Get text after from:

                                    if (dataFromClient.ToUpper().Contains("SIZE="))
                                    { //Oh cool, the client sent an expected message length. Lets see if it doesn't reach any of our limits
                                        try
                                        {
                                            string mailSize = dataFromClient.Remove(0, dataFromClient.Replace("size=", "SIZE=").LastIndexOf("SIZE=") + 5);
                                            if (mailSize.Contains(" "))
                                            {
                                                mailSize = mailSize.Remove(mailSize.IndexOf(" ")); //Remove everything after the space as it might be another command 
                                            }

                                            messagesizeLong = Convert.ToInt64(mailSize);
                                            if (messagesize < 0) //Uh, it seems we got a number back less then zero? Did the client send a malformed request?
                                            {
                                                sendTCP("501 Coudn't parse SIZE= extension");
                                            }
                                            else if (messagesizeLong > Int32.MaxValue) //We can't save this in our int32 and should be rejected anyways. A message this big is no good
                                            {
                                                sendTCP("552 Message too big");
                                                messagesize = -1;
                                            }
                                            else
                                            {
                                                messagesize = Convert.ToInt32(mailSize);
                                                mailFrom = mailFrom.Replace(" SIZE=" + mailSize, "").Replace(" size=" + mailSize, ""); //Remove size from mailFrom
                                            }

                                        }
                                        catch //Something went wrong, either SIZE= was followed by something thats not a number or we had an interger overflow (Which should be rejected too)
                                        {
                                            //Whoops error while checking, lets assume the command misformed
                                            sendTCP("501 Coudn't parse SIZE= extension");
                                            messagesize = -1; //Set to less then zero so the following commands don't get executed
                                        }
                                    }
                                    if (messagesize > maxDataSize) //The specefied messagesize is bigger then the maximum allowed, reject!
                                    {
                                        sendTCP("552 Message too big");
                                    }
                                    else if (messagesize >= 0)
                                    {
                                        Boolean bodytypeOK = true; //Set to false if we found an error while processing the BODY= parameter so we can stop the process in time
                                        if (dataFromClient.ToUpper().Contains("BODY=")) //Check if the client used a BODY= parameter
                                        {
                                            string bodyType = dataFromClient.Remove(0, dataFromClient.Replace("body=", "BODY=").LastIndexOf("BODY="));
                                            if (bodyType.Contains(" "))
                                            {
                                                bodyType = bodyType.Remove(bodyType.IndexOf(" ")); //Remove everything after the space as it might be another command 
                                            }

                                            if (bodyType.Length > 5) //Check if there is more then just "BODY="
                                            {
                                                bodyType = bodyType.Replace("BODY=", "").Replace("body=", ""); //Remove BODY=
                                                if (bodyType.ToUpper() == "8BITMIME" || bodyType.ToUpper() == "7BIT") //Check if body is a format we support (8BITMIME or 7BIT which is like 8-bit but with less supported characters)
                                                {
                                                    bodytype = bodyType.ToUpper();
                                                    mailFrom = mailFrom.Replace(" BODY=" + bodyType, "").Replace(" body=" + bodyType, ""); //Remove body=bodyType from mail address
                                                }
                                                else
                                                {
                                                    sendTCP("504 Unknown bodytype '" + bodyType + "'");
                                                    bodytypeOK = false;
                                                }
                                            }
                                            else //bodyType has 5 or less characters, this means there is no body specefied / malformed command
                                            {
                                                sendTCP("501 Syntax: RCPT TO:<address> <SP> <parameters>");
                                                bodytypeOK = false;
                                            }
                                        }
                                        if (bodytypeOK) //Make sure the bodytypeOK Boolean hasn't been set to false, indicating a problem was found and the command should stop
                                        {
                                            if (mailFrom.Contains(" "))
                                            {
                                                sendTCP("501 Syntax: MAIL FROM:<address>"); //Should NOT contain spaces!
                                            }
                                            else if (mailFrom == "<>") //Are we using an empty return address?
                                            {
                                                messagesizeExpected = messagesize;
                                                sendTCP("250 Ok (Empty return address - <>)");
                                            }
                                            else
                                            {

                                                mailFrom = mailFrom.Trim('<'); //Not sure if this is the best way to store adresses without brackets, but it will do for now.
                                                mailFrom = mailFrom.Trim('>');
                                                if (mailFrom.Length >= 1)
                                                { //If there is still one character left, accept and Ok.
                                                    messagesizeExpected = messagesize;
                                                    sendTCP("250 Ok");
                                                }
                                                else { sendTCP("501 Syntax: MAIL FROM:<address>"); } //After space removal, there is no address left
                                            }
                                        }
                                    }
                                }
                                else { sendTCP("530 5.7.0  Authentication required"); }
                            }
                            else { sendTCP("501 Syntax: MAIL FROM:<address>"); } //Malformed request?
                        }
                        else { sendTCP("501 Syntax: MAIL FROM:<address>"); } //Message too short and/or no from in message
                    }
                }
                // RCPT (To:<address>)
                else if (dataFromClient.Substring(0, 4).ToUpper() == "RCPT")
                {
                    if (heloFrom == "")
                    {
                        sendTCP("503 Please send HELO/EHLO first");
                    }
                    else if (mailFrom == "") //Need to set mail from first!!
                    {
                        sendTCP("503 Need MAIL command first");
                    }
                    else if (dataFromClient.Length >= 9 && dataFromClient.ToUpper().Contains("RCPT TO:"))
                    {
                        if (dataFromClient.Substring(5, 3).ToUpper() == "TO:")
                        {
                            string rcptMailBox = dataFromClient.Substring(8, (dataFromClient.Length - 8));
                            rcptMailBox = rcptMailBox.Trim('<'); //Not sure if this is the best way to store adresses without brackets, but it will do for now.
                            rcptMailBox = rcptMailBox.Trim('>');
                            if (rcptMailBox.Length >= 1)
                            {
                                Boolean addressOK = false;

                                if (rcptMailBox.Contains(":")) //Lets assume its a source route (Which should be stripped, following RFC2821 4.1.1.3, page 32) - Also the : char is not allowed in an address, so at worst we damage an already invalid address
                                {
                                    rcptMailBox = rcptMailBox.Substring(rcptMailBox.IndexOf(':') + 1, rcptMailBox.Length - rcptMailBox.IndexOf(':') - 1);
                                }

                                if (rcptMailBox.Contains("@")) //See if we have a domain listed in the address, because if not we should assume local
                                {
                                    addressOK = checkAddressSyntax(rcptMailBox, true); //Check if address correct
                                }
                                else
                                {
                                    addressOK = checkAddressSyntax(rcptMailBox + "@" + smtpHostname, true); //Check if address is correct, implying its an existing local address
                                }

                                if (addressOK)
                                {
                                    if (canAddRecipients(true)) //Are there any recipient slots left? 
                                    {
                                        if (rcptAvaliable(rcptMailBox) == 1) //rcptAvaliable returned 1, the exact email address is avaliable
                                        {
                                            addToRcptList(rcptMailBox);
                                            sendTCP("250 Ok <" + rcptMailBox + ">");
                                        }
                                        else if (rcptAvaliable(rcptMailBox) == 2) //rcptAvaliable returned 2, the email address is avaliable after we've added our hostname
                                        {
                                            addToRcptList(rcptMailBox + "@" + smtpHostname);
                                            sendTCP("250 Ok <" + rcptMailBox + "@" + smtpHostname + ">");
                                        }
                                        else //Reject recipient address
                                        {
                                            sendTCP("550 <" + rcptMailBox + ">: Recipient address rejected: User unknown in local recipient table"); //User was not found (rcptAvaliable returned 0, reject)
                                        }
                                    }
                                }
                            }
                            else { sendTCP("501 Syntax: RCPT TO:<address>"); } //RCPT has no characters left after removing spaces and <>
                        }
                        else { sendTCP("501 Syntax: RCPT TO:<address>"); } //RCPT was not followed by to: - Rejecting
                    }
                    else { sendTCP("501 Syntax: RCPT TO:<address>"); } //Message too short or no rcpt to: in message
                }
                // DATA
                else if (dataFromClient.Substring(0, 4).ToUpper() == "DATA")
                {
                    if (heloFrom == "")
                    {
                        sendTCP("503 Please send HELO/EHLO first");
                    }
                    else
                    {
                        if (dataFromClient.Length > 4) //DATA command shoudn't be longer then just "DATA"
                        {
                            sendTCP("501 Syntax: DATA");
                        }
                        else
                        {
                            if (userMailBox != "") //Make sure there is atleast one recipient
                            {
                                timeoutTimer.Interval = timeoutDATAInit; //Reset the timer to 0 again since we received a command
                                if (bodytype.Length > 0) //Check if we have specified a bodytype
                                {
                                    sendTCP("354 End " + bodytype + " data with <CRLF>.<CRLF> when done");
                                }
                                else
                                {
                                    sendTCP("354 End data with <CRLF>.<CRLF> when done");
                                }
                                currentlyHandlingData = true; //Set to true to make sure new data actually gets threated as data and not as commands!
                                dataStream = new MemoryStream();
                                dataStreamWriter = new StreamWriter(dataStream);
                                dataStreamReader = new StreamReader(dataStream);
                            }
                            else //No recipient(s)? Ask the client to use the RCPT command first!
                            {
                                sendTCP("503 No (valid) recipients, please use RCPT command first");
                            }
                        }
                    }
                }
                // VRFY
                else if (dataFromClient.Substring(0, 4).ToUpper() == "VRFY")
                {
                    if (dataFromClient.Length > 5) //Check if anything follows after VRFY (VRFY + one space = 5 characters)
                    {
                        string address = dataFromClient.Substring(5, (dataFromClient.Length - 5)); //Whats left after the command and one space
                        address = address.Trim('<'); //Not sure if this is the best way to store adresses without brackets, but it will do for now.
                        address = address.Trim('>');
                        Boolean addressOK = false;

                        if (address.Contains("@")) //See if we have a domain listed in the address, because if not we should assume local
                        {
                            addressOK = checkAddressSyntax(address, true); //Check if address correct
                        }
                        else
                        {
                            addressOK = checkAddressSyntax(address + "@" + smtpHostname, true); //Check if address is correct, implying its an existing local address
                        }

                        if (addressOK) //Address correct, continue
                        {
                            if (rcptAvaliable(address) == 1) //Address corrent and found
                            {
                                sendTCP("250 <" + address + ">");
                            }
                            else if (rcptAvaliable(address) == 2) //Address not correct but since adding our hostname gives a match we will assume its meant for us
                            {
                                sendTCP("250 <" + address + "@" + smtpHostname + ">");
                            }
                            else //Unknown recipient, reject!
                            {
                                sendTCP("550 <" + address + ">: Recipient address rejected: User unknown in local recipient table");
                            }
                        }
                    }
                    else //Nothing was followed by VRFY, reject command and ask for address
                    {
                        sendTCP("501 Syntax: VRFY <address>");
                    }
                }
                //HELP
                else if (dataFromClient.Substring(0, 4).ToUpper() == "HELP")
                {
                    string commandProvided = "";
                    if (dataFromClient.Length <= 5) //If the command is 5 characters (HELP + one space) or less, show default response
                    {
                        sendTCP("214-This server supports the following commands:");
                        sendTCP("214-(Use HELP <command> for more information)");
                        sendTCP("214 HELO EHLO RCPT DATA RSET MAIL QUIT HELP VRFY NOOP");
                    }
                    else
                    {
                        commandProvided = dataFromClient.Substring(5, dataFromClient.Length - 5).ToUpper();
                    }

                    if (commandProvided != "") //If commandProvided is not empty, try to reply with more information about this 
                    {
                        if (commandProvided == "HELO")
                        {
                            sendTCP("214-HELO (Basic SMTP Hello) - Syntax: HELO <hostname>");
                            sendTCP("214 HELO - Possible responses: 250 (OK), 501 (Invalid syntax)");
                        }
                        else if (commandProvided == "EHLO")
                        {
                            sendTCP("214-EHLO (Extended SMTP Hello) - Syntax: EHLO <hostname>");
                            sendTCP("214 EHLO - Possible responses: 250 (OK), 501 (Invalid syntax)");
                        }
                        else if (commandProvided == "QUIT")
                        {
                            sendTCP("214-QUIT - Syntax: QUIT");
                            sendTCP("214 QUIT - Possible responses: 221 (Connection Closing)");
                        }
                        else if (commandProvided == "MAIL")
                        {
                            sendTCP("214-MAIL - Syntax: MAIL FROM:<address> (<parameter(s)>)");
                            sendTCP("214-MAIL - Parameter: SIZE=<size in bytes>");
                            sendTCP("214-MAIL - Parameter: BODY=<8BITMIME/7BIT>");
                            sendTCP("214-MAIL - Possible responses: 250 (Ok), 501 (Syntax error)");
                            sendTCP("214 MAIL - Possible responses: 504 (Unknown bodytype), 552 (Message too big)");
                        }
                        else if (commandProvided == "RCPT")
                        {
                            sendTCP("214-RCPT - Syntax: RCPT TO:<address>");
                            sendTCP("214-RCPT - Possible responses: 250 (Ok), 501 (Syntax error)");
                            sendTCP("214 RCPT - Possible responses: 503 (Need MAIL first), 550 (Unknown recipient)");
                        }
                        else if (commandProvided == "DATA")
                        {
                            sendTCP("214-DATA - Syntax: DATA");
                            sendTCP("214-DATA - Possible responses: 354 (Ok/Ready), 501 (Syntax error)");
                            sendTCP("214 DATA - Possible responses: 503 (No recipients listed)");
                        }
                        else if (commandProvided == "VRFY")
                        {
                            sendTCP("214-VRFY - Syntax: VRFY <address>");
                            sendTCP("214 VRFY - Possible responses: 250 (Ok), 501 (Syntax error), 550 (Rejected)");
                        }
                        else if (commandProvided == "HELP")
                        {
                            sendTCP("214-HELP - Syntax: HELP (<command>)");
                            sendTCP("214 HELP - Possible responses: 214 (Information)");
                        }
                        else if (commandProvided == "NOOP")
                        {
                            sendTCP("214-NOOP (PING) - Syntax: NOOP");
                            sendTCP("214 NOOP - Possible responses: 250 (Ok)");
                        }
                        else if (commandProvided == "RSET")
                        {
                            sendTCP("214-RSET (RESET) - Syntax: RSET");
                            sendTCP("214 RSET - Possible responses: 250 (Ok)");
                        }
                        else
                        {
                            sendTCP("214 Command not found or no HELP information avaliable");
                        }
                    }
                }
                // NOOP
                else if (dataFromClient.Substring(0, 4).ToUpper() == "NOOP") //Ping!
                {
                    sendTCP("250 Ok");
                }
                // RSET
                else if (dataFromClient.Substring(0, 4).ToUpper() == "RSET") //Reset MAIL and RCPT (Should reset all input following RFC2821)
                {
                    userMailBox = "";
                    mailFrom = "";
                    sendTCP("250 Ok");

                }
                // B64E
                else if (dataFromClient.Substring(0, 4).ToUpper() == "B64E") //Will be removed once I got basr64 AUTH PLAIN implemented, just for debugging
                {
                    sendTCP("250-'" + dataFromClient.Substring(5, dataFromClient.Length - 5) + "'");
                    sendTCP("250 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(dataFromClient.Substring(5, dataFromClient.Length - 5)), Base64FormattingOptions.None));
                }
                // B64D
                else if (dataFromClient.Substring(0, 4).ToUpper() == "B64D") //Will be removed once I got basr64 AUTH PLAIN implemented, just for debugging
                {
                    string decoded = "";
                    byte[] decodedArray = Convert.FromBase64String(dataFromClient.Substring(5, dataFromClient.Length - 5));

                    foreach (byte decodedByte in decodedArray)
                    {
                        decoded += Convert.ToInt32(decodedByte).ToString() + " ";
                    }
                    sendTCP("250-" + decoded);
                    sendTCP("250 " + Encoding.UTF8.GetString(Convert.FromBase64String(dataFromClient.Substring(5, dataFromClient.Length - 5))));
                }
                // AUTH
                else if (dataFromClient.Substring(0, 4).ToUpper() == "AUTH")
                {
                    if (heloFrom != "")
                    {
                        if (userAuthenticated == false)
                        {
                            if (dataFromClient.Length >= 10)
                            {
                                if (dataFromClient.Substring(5, 5).ToUpper() == "PLAIN")
                                {
                                    if (dataFromClient.Length >= 12)
                                    {
                                        try
                                        {
                                            string authBase64 = dataFromClient.Remove(0, 11);

                                            authBase64 = Encoding.UTF8.GetString(Convert.FromBase64String(authBase64));

                                            string[] authBase64Split = authBase64.Split('\0');

                                            Thread.Sleep(300); //Sleep for 300ms, this makes it harder for bruteforcing to be profitable.

                                            if (authBase64Split.Length == 3 && (authBase64Split[1] + ":" + authBase64Split[2]) == credentials)
                                            {
                                                userAuthenticated = true;
                                                userAuthenticatedAs = authBase64Split[1];
                                                sendTCP("235 Authentication successful, welcome " + authBase64Split[1]);
                                            }
                                            else
                                            {
                                                sendTCP("535 Authentication failed");
                                            }
                                        }
                                        catch
                                        {
                                            sendTCP("535 Authentication failed");
                                        }
                                    }
                                    else
                                    {
                                        currentlyAuthenticating = true;
                                        sendTCP("334"); //Tell the client we're ready
                                    }
                                }
                                else
                                {
                                    sendTCP("535 Unknown / Unsupported authentication mechanism");
                                }
                            }
                            else
                            {
                                sendTCP("501 Syntax: AUTH <type>");
                            }
                        }
                        else
                        {
                            sendTCP("503 Already authenticated");
                        }
                    }
                    else
                    {
                        sendTCP("503 Please send HELO/EHLO first");
                    }
                }
                else
                {
                    sendTCP("500 Unknown command"); //After checking all other commands none of them matched, error.
                }
            }
            else if (dataFromClient.Length + 4 > commandlineMax) //Command is too big!
            {
                sendTCP("500 Line too long (Max " + commandlineMax + " total characters, you've got " + (dataFromClient.Length + 4) + " )");
            }
            else if (dataFromClient.Length > 0) //No command is that short other then possible DATA fragments. Error
            {
                sendTCP("500 Unknown command");
            }
        }

        private void sendTCP(string dataToSend)
        {
            Byte[] sendBytes = Encoding.UTF8.GetBytes(dataToSend + Environment.NewLine); //Add a new line (CRLF)
            try
            {
                networkStream.Write(sendBytes, 0, sendBytes.Length);
                networkStream.Flush();
                Console.WriteLine(">> " + dataToSend);
            }
            catch (Exception ex)
            {
                Console.WriteLine(">> An error occured while writing data to the networkStream" + Environment.NewLine + ex.ToString());
            }
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

        private Boolean canAddRecipients(Boolean shouldHandleErrorResponse = false)
        {
            //Check if we're not at our maximum recipients

            if (userMailBox.Contains(",")) //Check if there are is a split character in the userMailBox variable, indicating more then one email
            {
                int count = 0;
                string[] recipientlistSplit = Regex.Split(userMailBox, ",");
                
                foreach (string recipient in recipientlistSplit)
                {
                    count++; //It might be better to just have an interger that keeps track of the amount of recipients? Should look into this.
                }

                if (count < recipientsMax) //If count is less then maximum recipients
                {
                    return true;
                }
                else //If count is equal (maximum reached) or even higher
                {
                    if (shouldHandleErrorResponse)
                    {
                        sendTCP("452 Too many recipients (Max " + recipientsMax + " allowed)");
                    }
                    return false;
                }
            }
            else if (recipientsMax > 1)
            {
                return true; //There is only one or no recipients added and more then one is allowed
            }
            else
            {
                if (shouldHandleErrorResponse)
                {
                    sendTCP("452 Too many recipients (Max " + recipientsMax + " allowed)");
                }
                return false; //Maximum recipients only allows one or no(?) recipients
            }
        }

        private Boolean checkAddressSyntax(string address, Boolean shouldHandleErrorResponse = false)
        {
            //Check domain legth accordingly

            string[] addressSplit = address.Split('@');
            if (addressSplit.Length == 2) { if (addressSplit[1].Length > domainLengthMax) { if (shouldHandleErrorResponse) { sendTCP("501 Bad Address Syntax"); } return false; } }

            //Check Characters / Mailbox / Domain (All in one regex)
            //http://www.ex-parrot.com/~pdw/Mail-RFC822-Address.html (Added regex to resource file)
            var regexMatch = Regex.Match(address, Properties.Resources.addressRegex);
            if (regexMatch.Success && regexMatch.Value.Length == address.Length) { return true; } else { if (shouldHandleErrorResponse) { sendTCP("501 Bad Address Syntax"); } return false; }
        }

        private int rcptAvaliable(string rcpt)
        {
            //Return:
            //0: Not found / do not accept / false
            //1: Mailbox found, rcpt written correctly / true
            //2: Mailbox found, rcpt missing @<hostname> should add.

            //Check if mailbox is avaliable, if so return true
            if (avaliableMailBox.Contains(",")) //Check if our avaliableMailBox string contains more then one mailbox
            {
                string[] mailBoxes = Regex.Split(avaliableMailBox, ",");

                foreach (string mailBox in mailBoxes)
                {
                    if (mailBox == rcpt)
                    {
                        return 1; //Mailbox found and exsting
                    }
                    else if (mailBox == (rcpt + "@" + smtpHostname))
                    {
                        return 2; //Mailbox found after adding our hostname to it, assume its for us but report back that its not a 1:1 match
                    }
                }
                return 0; //Mailbox not found on this system
            }
            else
            {
                if (avaliableMailBox != "") //See if we have any mailboxes AT ALL
                {
                    if (avaliableMailBox == rcpt) //See if our only mailbox matches the current rcpt
                    {
                        return 1; //Mailbox found and exsting
                    }
                    else if (avaliableMailBox == (rcpt + "@" + smtpHostname))
                    {
                        return 2; //Mailbox found after adding our hostname to it, assume its for us but report back that its not a 1:1 match
                    }
                    else
                    {
                        return 0; //Mailbox not found on this system
                    }
                }
                else
                {
                    return 0; //There was no mailboxes! The answer is clear, there is no mailbox to be found.
                }
            }
        }

        public Boolean storeMessage(string content, int connectionid){
            if (storageMode == "file")
            {
                if (!Directory.Exists(@"mail"))
                {
                    try
                    {
                        Directory.CreateDirectory("mail");
                    }
                    catch
                    {
                        Console.WriteLine(">> Message couldn't be saved - Can't create mailroot directory");
                        return false;
                    }
                }

                foreach (string recipient in recipients())
                {
                    if (!Directory.Exists(@"mail\" + recipient))
                    {
                        try
                        {
                            Directory.CreateDirectory(@"mail\" + recipient);
                            if (debug == true)
                            {
                                Console.WriteLine(">> Created folder at:" + Environment.NewLine + ">> " + @"mail\" + recipient);
                            }
                        }
                        catch
                        {
                            Console.WriteLine(">> Message couldn't be saved - Can't create user directory");

                            //QUEUE MAIL UNDELIVERABLE RETURN TO SENDER HERE
                        }
                    }

                    try
                    {

                        File.WriteAllText(@"mail\" + recipient + @"\" + timestamp().ToString() + "-" + connectionid + ".eml", content);
                        if (debug == true)
                        {
                            Console.WriteLine(">> Written email to:" + Environment.NewLine + ">> " + @"mail\" + recipient + @"\" + timestamp().ToString() + "-" + connectionid + ".eml");
                        }
                    }
                    catch
                    {
                        Console.WriteLine(">> Coudn't write mail for recipient \"" + recipient + "\"");

                        //QUEUE MAIL UNDELIVERABLE RETURN TO SENDER HERE
                    }
                }
                return true;
            }
            else
            {
                Console.WriteLine(">> Message could not be saved - Unknown storage mode \"" + storageMode + "\"");
                return false;
            }
        }

        public string[] recipients()
        {
            if (userMailBox.Contains(",")) //Check if we have multiple recipients
            {
                return userMailBox.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            }
            else //No multiple recipients, just write down the string back in an array
            {
                return new[] { userMailBox };
            }
        }

        public Int32 timestamp()
        {
            return (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
        }
    }
}
