//Useful: http://www.samlogic.net/articles/smtp-commands-reference.htm
//TO DO
//Storage of mail
//Email forwarding for non local addresses
//Support for mailing lists? (Also EXPN command)
//Enable switch for allowing relay, make VRFY awnser with 554 if relay access is disabled (251 if accepted / will attempt), deny RCPT TO: etc. 
//Let server accept empty MAIL From: (MAIL From:<>)
//Implement EHLO (EHLO Size, etc.)


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
        int bufferSize = 1024 * 4; //4KB buffer, should probably be increased. Seems to have speed increase at receiving of DATA
        Boolean debug = false; //Set to true to have a more verbose output to the console
            
        string smtpHostname = "localhost"; //Hostname of this SMTP server
        string avaliableMailBox = "bart@localhost,root@localhost,postmaster@localhost"; //Just for testing (Will replace with proper database / storage solution later)

        string userMailBox = ""; //The recipient mailbox(es)
        string messageData = ""; //The message's DATA
        string heloFrom = ""; //Name that was entered during HELO command
        string mailFrom = ""; //Address from MAIL FROM:<address> command

        Boolean messageDataEmpty = true;
        Boolean currentlyHandlingData = false; //Mark true to accept following messages as DATA (Since they won't end with an \r and otherwise would get rejected)

        TcpClient clientSocket;
        NetworkStream networkStream;

        int dataSize = 0; //Semi accurate Size counter by increasing by *buffersize* on every run (Does also count null characters at the moment)
        MemoryStream dataStream; //To use while processing the DATA being received
        StreamWriter dataStreamWriter; //Streamwriter used to write to the memorystream
        StreamReader dataStreamReader; //Streamreader used to read the memorystream

        //Connection limits as by RFC2821 ----- NOT IMPLEMENTED IN CODE YET!!!
        int domainLengthMax = 255; //Maximum domainname size (characters after @)
        int commandlineMax = 512; //Maximum length of commandline, so the whole line (including command word and CRLF)
        int maxDataSize = 20; //Value in MB, gets convered to bytes on load
        int recipientsMax = 4000; //Should accept minimum of 100 as by RFC2821. No maximum listed.

        public void startClient(TcpClient inClientSocket)
        {
            this.clientSocket = inClientSocket;
            Thread t = new Thread(handleTCP);
            t.Start(); //Start new thread to handle the connection and return to normal operation
        }

        private void handleTCP()
        {
            byte[] bytesFrom = new byte[bufferSize];
            string dataFromClient = null;
            maxDataSize = 1024 * 1024 * maxDataSize; //Maximum of 20MB emails

            Boolean handeledSMTPHandshake = false;

            while ((clientSocket.Client.Connected))
            {
                try
                {
                    networkStream = clientSocket.GetStream();

                    if (handeledSMTPHandshake == false) { sendTCP("220 " + smtpHostname + " (EasyMail Indev)"); handeledSMTPHandshake = true; } //Only handshake once :)

                    if (networkStream.DataAvailable)
                    {
                        networkStream.Read(bytesFrom, 0, bufferSize);
                        dataFromClient = System.Text.Encoding.UTF8.GetString(bytesFrom); //Convert the byte array to a UTF8 string
                        bytesFrom = new byte[bufferSize]; //Clear byte array to erase previous messages
                        if (dataFromClient.Contains("\r\n") && currentlyHandlingData == false) //Only process data if it contains a newline and is not a DATA package
                        {
                            dataFromClient = dataFromClient.Substring(0, dataFromClient.IndexOf("\r\n"));
                            if (dataFromClient != "") { if (debug == true) { Console.WriteLine("|DEBUG|" + dataFromClient); } handleSMTP(dataFromClient); }
                        }
                        else if (currentlyHandlingData == true) //Process because it should be DATA
                        {
                            if (dataFromClient != "") { if (debug == true) { Console.WriteLine("|DEBUG|" + dataFromClient); } handleSMTP(dataFromClient); }
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
            Console.WriteLine(">> Connection closed");
        }

        private void handleSMTP(string dataFromClient)
        {
            if (debug) { Console.WriteLine(">> " + "Data received from client"); }

            if (currentlyHandlingData == true) //We're supposed to be handeling DATA, do not check for commands
            {
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
                            else
                            {
                                finalString = ""; //Empty the string, we don't need to interpent the endOfData dot as a part of the message
                                endOfData = true;
                            }
                        }
                    }
                    if (finalString != "" && endOfData == false) //Only process if it has not reached the end of data dot. (Without it also starts processing additional headers from the mail clients connection or null characters)
                    {
                        if (!messageDataEmpty) { dataStreamWriter.Write("\r\n"); } //Add newline since there is already data from previous DATA packages
                        dataStreamWriter.Write(finalString); //Add the line to the main DATA string
                        messageDataEmpty = false;
                    }
                }

                dataSize = (dataSize + bufferSize);

                if (dataSize > maxDataSize)
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
                }else if (endOfData) //If the end of data dot was received, send message and clear variables
                {
                    dataStreamWriter.Flush();
                    dataStream.Seek(0, SeekOrigin.Begin);
                    messageData = dataStreamReader.ReadToEnd();

                    if (debug == true) { Console.WriteLine("|DEBUG| Final DATA output:" + Environment.NewLine + messageData); }
                    currentlyHandlingData = false;
                    userMailBox = "";
                    mailFrom = "";
                    messageData = "";
                    messageDataEmpty = true;
                    dataSize = 0;
                    dataStreamWriter.Close();
                    dataStreamReader.Close();
                    dataStream.Close();
                    sendTCP("250 Ok: Queued"); //This is a lie for now, it doesn't actually store messages - Will probably implement SQLite or custom format depending on if I feel like it. (Also I'm working on a IMAP server)
                }
            }
            else if (dataFromClient.Length >= 4)
            {
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
                //QUIT
                else if (dataFromClient.Substring(0, 4) == "QUIT")
                {
                    sendTCP("221 Bye"); //Servers SHOULD reply with a 221 status code, but its not explicitly needed. Its nice to do so though.
                    networkStream.Close(); //Close networkstream
                }
                //MAIL (From:<address>)
                else if (dataFromClient.Substring(0, 4) == "MAIL")
                {
                    if (dataFromClient.Length >= 11 && dataFromClient.ToLower().Contains("from:")) //Make sure the MAIL command actually includes a from:
                    {
                        if (dataFromClient.Substring(5, 5).ToLower() == "from:") //Make sure the from: is in the right position as expected
                        {
                            mailFrom = dataFromClient.Substring(10, (dataFromClient.Length - 10)); //Get text after from:
                            mailFrom = mailFrom.Trim(' '); //Remove any spaces that might be in there for whatever reason
                            mailFrom = mailFrom.Trim('<'); //Not sure if this is the best way to store adresses without brackets, but it will do for now.
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
                else if (dataFromClient.Substring(0, 4) == "RCPT")
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
                            rcptMailBox = rcptMailBox.Trim(' '); //Remove any spaces that might be in there for whatever reason 
                            rcptMailBox = rcptMailBox.Trim('<'); //Not sure if this is the best way to store adresses without brackets, but it will do for now.
                            rcptMailBox = rcptMailBox.Trim('>');
                            if (rcptMailBox.Length >= 1)
                            {
                                Boolean addressOK = false;
                                if (rcptMailBox.Contains("@"))
                                {
                                    addressOK = checkAddressSyntax(rcptMailBox, true); //Check if address correct
                                }
                                else
                                {
                                    addressOK = checkAddressSyntax(rcptMailBox + "@" + smtpHostname, true); //Check if address is correct, implying its an existing local address
                                }

                                if (addressOK)
                                {
                                    if (rcptAvaliable(rcptMailBox) == 1)
                                    {
                                        addToRcptList(rcptMailBox);
                                        sendTCP("250 Ok <" + rcptMailBox + ">");
                                    }
                                    else if (rcptAvaliable(rcptMailBox) == 2)
                                    {
                                        addToRcptList(rcptMailBox + "@" + smtpHostname);
                                        sendTCP("250 Ok <" + rcptMailBox + "@" + smtpHostname + ">");
                                    }
                                    else
                                    {
                                        sendTCP("550 <" + rcptMailBox + ">: Recipient address rejected: User unknown in local recipient table"); //User was not found (rcptAvaliable returned 0, reject)
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
                else if (dataFromClient.Substring(0, 4) == "DATA")
                {
                    if (dataFromClient.Length > 4) //DATA command shoudn't be longer then just "DATA"
                    {
                        sendTCP("501 Syntax: DATA");
                    }
                    else
                    {
                        if (userMailBox != "") //Make sure there is atleast one recipient
                        {
                            sendTCP("354 End data with <CRLF>.<CRLF> when done");
                            currentlyHandlingData = true; //Set to true to make sure new data actually gets threated as data and not as commands!
                            dataStream = new MemoryStream();
                            dataStreamWriter = new StreamWriter(dataStream);
                            dataStreamReader = new StreamReader(dataStream);
                        }
                        else //No recipient(s)? Ask the client to use the RCPT command first!
                        {
                            sendTCP("554 No (valid) recipients, please use RCPT command first");
                        }
                    }
                }
                else if (dataFromClient.Substring(0, 4) == "VRFY")
                {
                    if (dataFromClient.Length > 5) //Check if anything follows after VRFY (VRFY + one space = 5 characters)
                    {
                        string address = dataFromClient.Substring(5, (dataFromClient.Length - 5)); //Whats left after the command and one space
                        address = address.Trim('<'); //Not sure if this is the best way to store adresses without brackets, but it will do for now.
                        address = address.Trim('>');
                        Boolean addressOK = false;

                        if (address.Contains("@")){
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
                                sendTCP("250 " + address);
                            }
                            else if (rcptAvaliable(address) == 2) //Address not correct but since adding our hostname gives a match we will assume its meant for us
                            {
                                sendTCP("250 " + address + "@" + smtpHostname);
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
                else if (dataFromClient.Substring(0, 4) == "NOOP")
                {
                    sendTCP("250 Ok");
                }
                else
                {
                    sendTCP("500 Unknown command"); //After checking all other commands none of them matched, error.
                }
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

        private Boolean checkAddressSyntax(string address, Boolean shouldHandleErrorResponse = false)
        {
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
    }
}
