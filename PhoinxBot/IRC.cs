using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Data.SQLite;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;


namespace PhoinxBot
{
    class IRC
    {
        //Client vars
        TcpClient Client;
        NetworkStream NwStream;
        StreamReader Reader;
        StreamWriter Writer;

        //Special vars
        ConcurrentDictionary<string, bool> pollOpen;
        ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, int>>> pollVotes;
        ConcurrentDictionary<string, bool> antiSpoil;
        ConcurrentDictionary<string, ConcurrentDictionary<string, int>> blackList;

        //Spoiler regex
        Regex spoilers;

        //Listen thread
        Thread listen;

        string channel;

        //Constructor
        public IRC()
        {
            //Opens connection to the twitch IRC
            Client = new TcpClient("irc.twitch.tv", 443);
            NwStream = Client.GetStream();
            Reader = new StreamReader(NwStream, Encoding.GetEncoding("iso8859-1"));
            Writer = new StreamWriter(NwStream, Encoding.GetEncoding("iso8859-1"));

            //Starts a thread that reads all data from IRC
            listen = new Thread(new ThreadStart(Listen));
            listen.Start();

            //Special vars initialized
            pollOpen = new ConcurrentDictionary<string,bool>();
            pollVotes = new ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, int>>>();
            antiSpoil = new ConcurrentDictionary<string, bool>();
            blackList = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();

            //Writes userdata to twitch - remember your username and password!
            Writer.WriteLine("USER " + Properties.Settings.Default.Username + "tmi twitch :" + Properties.Settings.Default.Username);
            Writer.Flush();
            Writer.WriteLine("PASS " + Properties.Settings.Default.Password);
            Writer.Flush();
            Writer.WriteLine("NICK "+ Properties.Settings.Default.Username);
            Writer.Flush();

            //Regex to check for spoilers initialized
            spoilers = new Regex("^(.*)[0-9]+[_ ]*[-:][_ ]*[0-9](.*)$");

            //Initializes all channels
            using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
            {
                dbCon.Open();
                string query = "SELECT name FROM channels ORDER BY name ASC";
                SQLiteCommand command = new SQLiteCommand(query, dbCon);
                SQLiteDataReader reader = command.ExecuteReader();

                while (reader.Read())
                {
                    InitChan((string)reader["name"]);
                }
                dbCon.Close();
            }
        }

        //Initializes a new channel
        private void InitChan(string name)
        {
            pollOpen["#" + name] = false;
            pollVotes["#" + name] = null;
            antiSpoil["#" + name] = false;

            ConcurrentDictionary<string, int> tmpBL = new ConcurrentDictionary<string, int>();

            //Builds the chans blacklist table
            using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
            {
                dbCon.Open();
                string qSelect = "SELECT * FROM blacklist WHERE channel = '#" + name + "'";
                SQLiteCommand command = new SQLiteCommand(qSelect, dbCon);
                SQLiteDataReader reader = command.ExecuteReader();

                while (reader.Read())
                {
                    tmpBL.TryAdd((string)reader["text"], (int)reader["type"]);
                }
                dbCon.Close();
            }
            //Anti ascii spam thingy
            tmpBL.TryAdd("â", 0);

            //Adds to "global" scope var
            blackList.TryAdd("#" + name, tmpBL);

            Writer.WriteLine("JOIN #" + name + "\n");
            Writer.Flush();
        }

        //Checks is a person is op/owner in a channel
        private bool isOp(string channel, string username)
        {
            //If owner
            bool _xist = false;
            if(username == channel.Replace("#", "")) {
                return true;
            }

            //If op
            using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
            {
                dbCon.Open();
                string qSelect = "SELECT * FROM permits WHERE user = '" + username + "' AND channel = '" + channel + "'";
                SQLiteCommand command = new SQLiteCommand(qSelect, dbCon);
                SQLiteDataReader reader = command.ExecuteReader();
                while (reader.Read() && _xist == false)
                {
                    _xist = true;
                }
                dbCon.Close();
            }

            return _xist;
        }

        //Checks if a user is the owner
        private bool isOwner(string channel, string username)
        {
            return username == channel.Replace("#", "");
        }

        //Reads all the data from the IRC server
        public void Listen()
        {
            string Data = "";
            try
            {
                while ((Data = Reader.ReadLine()) != null)
                {
                    //Initializing vars
                    string _nick = "";
                    string _type = "";
                    string _channel = "";
                    string _message = "";
                    string[] ex;

                    //Writes to console and main terminal
                    Console.WriteLine(Data);
                    Program.mainForm.AddLine("", Data);

                    //Respond PINGs with PONGs and the same message
                    ex = Data.Split(new char[] { ' ' }, 5);
                    if (ex[0] == "PING")
                    {
                        SendMessage("PONG " + ex[1]);
                        Console.WriteLine("PONG " + ex[1]);
                    }

                    //Splitting message and the meta data
                    string[] split1 = Data.Split(':');
                    if (split1.Length > 1)
                    {
                        //Splitting nick, type, chan and message
                        string[] split2 = split1[1].Split(' ');

                        //Nick consists of various things - we only want the nick itself
                        _nick = split2[0];
                        _nick = _nick.Split('!')[0];

                        //Type = PRIVMSG for normal messages. Only thing we need
                        _type = split2[1];

                        //Channel posted to
                        _channel = split2[2];

                        //Get message
                        if (split1.Length > 2)
                        {
                            for (int i = 2; i < split1.Length; i++)
                            {
                                _message += split1[i] + " ";
                            }
                        }

                        //Print nicely in seperate channel
                        if (_type == "PRIVMSG" && _channel.Contains("#"))
                        {
                            Program.mainForm.AddLine(_channel, "<" + DateTime.Now.ToString("HH:mm:ss") + "> <" + _nick + "> " + _message);
                        }

                        /*
                        * CHECK FOR ALL THE VARIOUS FUNCTIONS IN THE BOT - THIS IS THE FUN PART!
                        */

                        //Custom commands
                        if (_message.StartsWith("!cmd") && isOp(_channel, _nick))
                        {
                            string[] cmd = _message.Replace("!cmd", "").TrimStart(' ').TrimEnd(' ').Split(' ');
                            if (cmd.Length > 1)
                            {
                                //Gets name and text from the cmd posted
                                string _name = cmd[0];
                                string _text = String.Join(" ", cmd).Replace(_name, "").TrimEnd(' ').TrimStart(' ');

                                //Check if command exists in channel
                                bool _xist = false;
                                using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
                                {
                                    dbCon.Open();
                                    string qSelect = "SELECT * FROM commands WHERE command = '" + _name + "' AND channel = '" + _channel + "'";
                                    SQLiteCommand command = new SQLiteCommand(qSelect, dbCon);
                                    SQLiteDataReader reader = command.ExecuteReader();
                                    while (reader.Read() && _xist == false)
                                    {
                                        _xist = true;
                                    }

                                    //Either update, remove or add it
                                    if (_xist)
                                    {
                                        string qUpdate = "";
                                        if (_text == "remove")
                                        {
                                            qUpdate = "DELETE FROM commands WHERE channel = '" + _channel + "' AND command = '" + _name + "'";
                                            Console.WriteLine("CMD DELETED!");
                                            SendMessage("Command " + _name + " deleted!");
                                        }
                                        else
                                        {
                                            qUpdate = "UPDATE commands SET text = '" + _text + "' WHERE channel = '" + _channel + "' AND command = '" + _name + "'";
                                            Console.WriteLine("CMD UPDATED! (" + _name + " -> " + _text + ")");
                                            SendMessage("Command " + _name + " updated!");
                                        }

                                        SQLiteCommand cUpdate = new SQLiteCommand(qUpdate, dbCon);
                                        cUpdate.ExecuteNonQuery();
                                    }
                                    else
                                    {
                                        string qInsert = "INSERT INTO commands (command, text, channel) VALUES ('" + _name + "', '" + _text + "', '" + _channel + "')";
                                        SQLiteCommand cInsert = new SQLiteCommand(qInsert, dbCon);
                                        cInsert.ExecuteNonQuery();
                                        Console.WriteLine("CMD ADDED! (" + _name + " -> " + _text + ")");
                                        SendMessage("Command " + _name + " added!");
                                    }
                                    dbCon.Close();
                                }
                            }
                        }

                        ////Poll commands
                        //else if (_message.StartsWith("!poll") && isOp(_channel, _nick))
                        //{
                        //    string[] cmd = _message.Replace("!poll", "").TrimStart(' ').TrimEnd(' ').Split(' ');
                        //    //If you just enter !poll it posts the current status (not running or cmds to vote with)
                        //    if (cmd[0] == "")
                        //    {
                        //        if (pollOpen[_channel])
                        //        {
                        //            string answers = "";
                        //            foreach (KeyValuePair<string, ConcurrentDictionary<string, int>> entry in pollVotes[_channel])
                        //            {
                        //                answers += "\"!" + entry.Key + "\", ";
                        //            }

                        //            answers = answers.TrimEnd(' ').TrimEnd(',');

                        //            SendMessage("You can vote using " + answers);
                        //        }
                        //        else
                        //        {
                        //            SendMessage("No poll running!");
                        //        }
                        //    }
                        //    //Open a poll with minimum 2 possible answers
                        //    else if (cmd[0] == "open" && cmd.Length > 2)
                        //    {
                        //        //Poll is already open
                        //        if (pollOpen[_channel])
                        //        {
                        //            SendMessage("Poll is already open!");
                        //        }
                        //        else
                        //        {
                        //            //Inserts the answers into our kinda deep array with strings as keys w00t
                        //            ConcurrentDictionary<string, ConcurrentDictionary<string, int>> votes = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();
                        //            for (int i = 1; i < cmd.Length; i++)
                        //            {
                        //                string tmpan = cmd[i].TrimEnd(' ').TrimStart(' ');
                        //                if (tmpan != "")
                        //                {
                        //                    votes[tmpan] = new ConcurrentDictionary<string, int>();
                        //                }
                        //            }

                        //            //Inserts this into our special vars
                        //            pollVotes[_channel] = votes;
                        //            pollOpen[_channel] = true;

                        //            //Posts the initial message - opened, how to vote
                        //            string answers = "";
                        //            foreach (KeyValuePair<string, ConcurrentDictionary<string, int>> entry in pollVotes[_channel])
                        //            {
                        //                answers += "\"!" + entry.Key + "\", ";
                        //            }

                        //            answers = answers.TrimEnd(' ').TrimEnd(',');
                        //            SendMessage("Poll started! You can vote using " + answers);
                        //        }
                        //    }
                        //    //Displays the current result (result/close) and can close if (close)
                        //    else if (cmd[0] == "result" || cmd[0] == "close")
                        //    {
                        //        //Poll not open
                        //        if (!pollOpen[_channel])
                        //        {
                        //            SendMessage("No poll running!");
                        //        }
                        //        else
                        //        {
                        //            //Array with the results
                        //            ConcurrentDictionary<string, int> res = new ConcurrentDictionary<string, int>();
                        //            int total = 0;

                        //            //Find the number of votes for each answer and gets the total amount of votes
                        //            foreach (KeyValuePair<string, ConcurrentDictionary<string, int>> entry in pollVotes[_channel])
                        //            {
                        //                res[entry.Key] = pollVotes[_channel][entry.Key].Count;
                        //                total += pollVotes[_channel][entry.Key].Count;
                        //            }

                        //            string votes = "";

                        //            //Then we round it and insert it nicely into a string
                        //            foreach (KeyValuePair<string, int> entry in res)
                        //            {
                        //                double pc = 0;
                        //                int perc = 0;

                        //                if (total > 0)
                        //                {
                        //                    pc = (double) entry.Value / (double) total * (double) 100;
                        //                    perc = (int) Math.Round(pc);
                        //                }

                        //                votes += "\"" + entry.Key + "\" (" + perc + "%), ";
                        //            }

                        //            votes = votes.TrimEnd(' ').TrimEnd(',');

                        //            //We either just display the result or display result AND close the poll
                        //            if (cmd[0] == "result")
                        //            {
                        //                SendMessage("Current result is: " + votes + " - Total votes: " + total);
                        //            }
                        //            else if (cmd[0] == "close")
                        //            {
                        //                pollOpen[_channel] = false;
                        //                pollVotes[_channel] = null;
                        //                SendMessage("Poll closed! Result is: " + votes + " - Total votes: " + total);
                        //            }
                        //        }
                        //    }
                        //}

                        //Blacklists certain words
                        else if (_message.StartsWith("!blist") && isOp(_channel, _nick))
                        {
                            string[] cmd = _message.Replace("!blist", "").TrimStart(' ').TrimEnd(' ').Split(' ');
                            if (cmd.Length > 1)
                            {
                                //Add or remove
                                if (cmd[0] == "add" && cmd.Length > 2 || cmd[0] == "remove")
                                {
                                    //If add you need to post the type aswell (ban, timeout)
                                    string _btype = "";
                                    string _btext = "";
                                    if (cmd[0] == "add")
                                    {
                                        _btype = cmd[1];
                                        _btext = cmd[2];

                                        if (_btype == "ban")
                                        {
                                            _btype = "1";
                                        }
                                        else
                                        {
                                            _btype = "0";
                                        }
                                    }
                                    else
                                    {
                                        _btext = cmd[1];
                                    }

                                    //Check if blacklist already exists
                                    bool _xist = false;
                                    using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
                                    {
                                        dbCon.Open();
                                        string qSelect = "SELECT * FROM blacklist WHERE text = '" + _btext + "' AND channel = '" + _channel + "'";
                                        SQLiteCommand command = new SQLiteCommand(qSelect, dbCon);
                                        SQLiteDataReader reader = command.ExecuteReader();
                                        while (reader.Read() && _xist == false)
                                        {
                                            _xist = true;
                                        }

                                        //Either add or remove it
                                        if (!_xist && cmd[0] == "add")
                                        {
                                            string qUpdate = "INSERT INTO blacklist (type, text, channel) VALUES ('" + _btype + "', '" + _btext + "', '" + _channel + "')";
                                            Console.WriteLine("Blacklist added! (" + _btext + ")");

                                            SQLiteCommand cUpdate = new SQLiteCommand(qUpdate, dbCon);
                                            cUpdate.ExecuteNonQuery();

                                            blackList[_channel].TryAdd(_btext, Convert.ToInt16(_btype));

                                            SendMessage("Blacklist for " + _btext + " added!");
                                        }
                                        else if (_xist && cmd[0] == "remove")
                                        {
                                            string qUpdate = "DELETE FROM blacklist WHERE text = '" + _btext + "' AND channel = '" + _channel + "'";
                                            Console.WriteLine("Blacklist removed! (" + _btext + ")");

                                            SQLiteCommand cUpdate = new SQLiteCommand(qUpdate, dbCon);
                                            cUpdate.ExecuteNonQuery();

                                            int Fail1;
                                            blackList[_channel].TryRemove(_btext, out Fail1);

                                            SendMessage("Blacklist for " + _btext + " removed!");
                                        }
                                        dbCon.Close();
                                    }
                                }
                            }
                            //Displays all the current blacklisted words
                            else if (cmd[0] == "show")
                            {
                                //Displays all the words nicely
                                string blist = "";
                                using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
                                {
                                    dbCon.Open();
                                    string qSelect = "SELECT * FROM blacklist WHERE channel = '" + _channel + "'";
                                    SQLiteCommand command = new SQLiteCommand(qSelect, dbCon);
                                    SQLiteDataReader reader = command.ExecuteReader();
                                    while (reader.Read())
                                    {
                                        blist += "\"" + reader["text"] + "\", ";
                                    }
                                    dbCon.Close();
                                }

                                blist = blist.TrimEnd(' ').TrimEnd(',');
                                SendMessage("Blacklist is: " + blist);
                            }
                        }

                        //Antispoiler command - timeouts people posting spoilers
                        else if (_message.StartsWith("!antispoil") && isOp(_channel, _nick))
                        {
                            string[] cmd = _message.Replace("!antispoil", "").TrimStart(' ').TrimEnd(' ').Split(' ');
                            if (cmd[0] == "on")
                            {
                                SendMessage("Anti-spoiler enabled! Avoid typing result-like comments - you will be timed out.");
                                antiSpoil[_channel] = true;
                            }
                            else if (cmd[0] == "off")
                            {
                                SendMessage("Anti-spoiler disabled! Go crazy!");
                                antiSpoil[_channel] = false;
                            }

                        }

                        //Adds a new op to the bot (can use all the above commands) - this can only be used by the channel owner
                        else if (_message.StartsWith("!permit") && isOwner(_channel, _nick))
                        {
                            string[] cmd = _message.Replace("!permit", "").TrimStart(' ').TrimEnd(' ').Split(' ');
                            Console.WriteLine(cmd.Length);
                            if (cmd.Length == 2)
                            {
                                //Check if user exists as mod
                                bool _xist = false;
                                using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
                                {
                                    dbCon.Open();
                                    string qSelect = "SELECT * FROM permits WHERE user = '" + cmd[1] + "' AND channel = '" + _channel + "'";
                                    SQLiteCommand command = new SQLiteCommand(qSelect, dbCon);
                                    SQLiteDataReader reader = command.ExecuteReader();
                                    while (reader.Read() && _xist == false)
                                    {
                                        _xist = true;
                                    }

                                    //Either add or remove him
                                    if (!_xist && cmd[0] == "add")
                                    {
                                        string qUpdate = "INSERT INTO permits (user, channel) VALUES ('" + cmd[1] + "', '" + _channel + "')";
                                        Console.WriteLine("USER ADDED! (" + cmd[1] + ")");

                                        SQLiteCommand cUpdate = new SQLiteCommand(qUpdate, dbCon);
                                        cUpdate.ExecuteNonQuery();
                                    }
                                    else if (_xist && cmd[0] == "remove")
                                    {
                                        string qUpdate = "DELETE FROM permits WHERE user = '" + cmd[1] + "' AND channel = '" + _channel + "'";
                                        Console.WriteLine("USER DELETED! (" + cmd[1] + ")");

                                        SQLiteCommand cUpdate = new SQLiteCommand(qUpdate, dbCon);
                                        cUpdate.ExecuteNonQuery();
                                    }
                                    dbCon.Close();
                                }
                            }

                        }

                        //Displays all the custom commands created with !cmd
                        else if (_message.StartsWith("!commands"))
                        {
                            string cmds = "";
                            //Displays them nicely
                            using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
                            {
                                dbCon.Open();
                                string qSelect = "SELECT * FROM commands WHERE channel = '" + _channel + "'";
                                SQLiteCommand command = new SQLiteCommand(qSelect, dbCon);
                                SQLiteDataReader reader = command.ExecuteReader();

                                while (reader.Read())
                                {
                                    cmds += "!" + reader["command"] + " | ";
                                }
                                cmds = cmds.TrimEnd(' ').TrimEnd('|').TrimEnd(' ');
                                dbCon.Close();
                            }
                            SendMessage("Commands are: " + cmds);
                        }

                        //Displays the current time in CEST
                        else if (_message.StartsWith("!time"))
                        {
                            SendMessage("Current time: " + DateTime.Now.ToString("HH:mm:ss") + " CEST");
                        }

                        //Check for votes and custom commands - votes will override custom cmds, since they are temporary
                        else if (_message.StartsWith("!"))
                        {
                            string _cmd = _message.Replace("!", "").TrimEnd(' ').TrimStart(' ');

                            ////Votes
                            //if (pollOpen[_channel])
                            //{
                            //    if (pollVotes[_channel].ContainsKey(_cmd))
                            //    {
                            //        string[] ans = _message.TrimEnd(' ').TrimStart(' ').Split(' ');
                            //        if (ans.Length > 1 && pollVotes[_channel].ContainsKey(ans[1]))
                            //        {
                            //            if (!pollVotes[_channel][ans[1]].ContainsKey(_nick))
                            //            {
                            //                foreach (KeyValuePair<string, ConcurrentDictionary<string, int>> entry in pollVotes[_channel])
                            //                {
                            //                    if (pollVotes[_channel][entry.Key].ContainsKey(_nick))
                            //                    {
                            //                        int Fail1;
                            //                        pollVotes[_channel][entry.Key].TryRemove(_nick, out Fail1);
                            //                    }
                            //                }
                            //                pollVotes[_channel][ans[1]].TryAdd(_nick, 1);
                            //            }
                            //        }
                            //    }
                            //}
                            ////Custom cmds
                            //else
                            //{
                                //Check if it exists and post the text
                                using (SQLiteConnection dbCon = new SQLiteConnection("Data Source=Database.sqlite;Version=3;"))
                                {
                                    dbCon.Open();
                                    string query = "SELECT * FROM commands WHERE channel = '" + _channel + "' AND command = '" + _cmd + "'";
                                    SQLiteCommand command = new SQLiteCommand(query, dbCon);
                                    SQLiteDataReader reader = command.ExecuteReader();

                                    string _text = "";

                                    while (reader.Read())
                                    {
                                        _text = (string)reader["text"];
                                    }

                                    if (_text != "")
                                    {
                                        SendMessage("" + _text);
                                    }
                                    dbCon.Close();
                                }
                            //}
                        }
                        //Check all messages for blacklisted commands (and ascii art-crap) and spoilers (if enabled)
                        else if(_channel.Contains('#'))
                        {
                            //Antispoiler
                            if (antiSpoil[_channel] && spoilers.IsMatch(_message))
                            {
                                Console.WriteLine("Spoiler by " + _nick);
                                SendMessage("/timeout " + _nick + " 1");
                                SendMessage("" + _nick + " no spoilers pls!");
                            }
                            
                            //Check blacklist
                            foreach(KeyValuePair<string, int> x in blackList[_channel])
                            {
                                if (_message.Contains(x.Key))
                                {
                                    if (x.Value == 1)
                                    {
                                        SendMessage("/ban " + _nick);
                                        SendMessage("" + _nick + " received ban for blacklisted message");
                                    }
                                    else
                                    {
                                        SendMessage("/timeout " + _nick + " 1");
                                        //SendMessage("" + _nick + " reveiced 1s timeout for blacklisted message");
                                    }
                                }
                            }   
                            channel = _channel;

                        }            

                    }
                }
            }
            catch (Exception e)
            {
                System.Windows.Forms.MessageBox.Show(e.Message);
                listen.Abort();
                listen.Start();
            }
        }

        //Sends a message and adds it to the mainform
        public void SendMessage(string message)
        {
            if (message != "")
            {
                Writer.WriteLine("PRIVMSG " + channel + " :" + message);
                Program.mainForm.AddLine("", "PRIVMSG " + channel + " :" + message);
                Program.mainForm.AddLine(channel, "<" + DateTime.Now.ToString("HH:mm:ss") + "> <" + Properties.Settings.Default.Username + "> " + message);
                Writer.Flush();
            }
        }
    }
}
