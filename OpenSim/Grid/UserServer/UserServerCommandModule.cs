/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Grid.Framework;
using OpenSim.Grid.UserServer.Modules;

namespace OpenSim.Grid.UserServer
{
    public class UserServerCommandModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected CommandConsole m_console;
        protected UserConfig m_cfg;

        protected UserDataBaseService m_userDataBaseService;
        protected UserLoginService m_loginService;

        protected UUID m_lastCreatedUser = UUID.Random();

        protected IGridServiceCore m_core;

        public UserServerCommandModule()
        {
        }

        public void Initialize(IGridServiceCore core)
        {
            m_core = core;
        }

        public void PostInitialize()
        {
            UserConfig cfg;
            if (m_core.TryGet<UserConfig>(out cfg))
            {
                m_cfg = cfg;
            }

            UserDataBaseService userDBservice;
            if (m_core.TryGet<UserDataBaseService>(out userDBservice))
            {
                m_userDataBaseService = userDBservice;
            }

            UserLoginService loginService;
            if (m_core.TryGet<UserLoginService>(out loginService))
            {
                m_loginService = loginService;
            }

            CommandConsole console;
            if ((m_core.TryGet<CommandConsole>(out console)) && (m_cfg != null)
                && (m_userDataBaseService != null) && (m_loginService != null))
            {
                RegisterConsoleCommands(console);
            }
        }

        public void RegisterHandlers(BaseHttpServer httpServer)
        {

        }

        private void RegisterConsoleCommands(CommandConsole console)
        {
            m_console = console;
            m_console.Commands.AddCommand("userserver", false, "create user",
                    "create user [<first> [<last> [<pass> [<x> <y> [<email>]]]]]",
                    "Create a new user account", RunCommand);

            m_console.Commands.AddCommand("userserver", false, "reset user password",
                    "reset user password [<first> [<last> [<new password>]]]",
                    "Reset a user's password", RunCommand);

            m_console.Commands.AddCommand("userserver", false, "login level",
                    "login level <level>",
                    "Set the minimum user level to log in", HandleLoginCommand);

            m_console.Commands.AddCommand("userserver", false, "login reset",
                    "login reset",
                    "Reset the login level to allow all users",
                    HandleLoginCommand);

            m_console.Commands.AddCommand("userserver", false, "login text",
                    "login text <text>",
                    "Set the text users will see on login", HandleLoginCommand);

            m_console.Commands.AddCommand("userserver", false, "test-inventory",
                    "test-inventory",
                    "Perform a test inventory transaction", RunCommand);

            m_console.Commands.AddCommand("userserver", false, "logoff-user",
                    "logoff-user <first> <last> <message>",
                    "Log off a named user", RunCommand);

            m_console.Commands.AddCommand("default", false, "default regions",
                                          "default regions [<filename>]",
                                          "Show or set the default region locations via a file of locations", HandleDefault);
            m_console.Commands.AddCommand("default", false, "default logins",
                                          "default logins [<filename>]",
                                          "Show or set the initial region locations for new users via a file of locations", HandleDefault);

            m_console.Commands.AddCommand("base", false, "trust reload",
                                          "trust reload",
                                          "Reloads the trust configuration for the trust manager", HandleTrustReload);
        }

        #region Console Command Handlers
        public void do_create(string[] args)
        {
            switch (args[0])
            {
                case "user":
                    CreateUser(args);
                    break;
            }
        }

        /// <summary>
        /// Execute switch for some of the reset commands
        /// </summary>
        /// <param name="args"></param>
        protected void Reset(string[] args)
        {
            if (args.Length == 0)
                return;

            switch (args[0])
            {
                case "user":

                    switch (args[1])
                    {
                        case "password":
                            ResetUserPassword(args);
                            break;
                    }

                    break;
            }
        }

        /// <summary>
        /// Create a new user
        /// </summary>
        /// <param name="cmdparams">string array with parameters: firstname, lastname, password, locationX, locationY, email</param>
        protected void CreateUser(string[] cmdparams)
        {
            string firstName;
            string lastName;
            string password;
            string email;
            uint regX = 1000;
            uint regY = 1000;

            if (cmdparams.Length < 2)
                firstName = MainConsole.Instance.CmdPrompt("First name", "Default");
            else firstName = cmdparams[1];

            if (cmdparams.Length < 3)
                lastName = MainConsole.Instance.CmdPrompt("Last name", "User");
            else lastName = cmdparams[2];

            if (cmdparams.Length < 4)
                password = MainConsole.Instance.PasswdPrompt("Password");
            else password = cmdparams[3];

            if (cmdparams.Length < 5)
                regX = Convert.ToUInt32(MainConsole.Instance.CmdPrompt("Start Region X", regX.ToString()));
            else regX = Convert.ToUInt32(cmdparams[4]);

            if (cmdparams.Length < 6)
                regY = Convert.ToUInt32(MainConsole.Instance.CmdPrompt("Start Region Y", regY.ToString()));
            else regY = Convert.ToUInt32(cmdparams[5]);

            if (cmdparams.Length < 7)
                email = MainConsole.Instance.CmdPrompt("Email", String.Empty);
            else email = cmdparams[6];

            if (null == m_userDataBaseService.GetUserProfile(firstName, lastName))
            {
                m_lastCreatedUser = m_userDataBaseService.AddUser(firstName, lastName, password, email, regX, regY);
            }
            else
            {
                m_log.ErrorFormat("[USERS]: A user with the name {0} {1} already exists!", firstName, lastName);
            }
        }

        /// <summary>
        /// Reset a user password.
        /// </summary>
        /// <param name="cmdparams"></param>
        private void ResetUserPassword(string[] cmdparams)
        {
            string firstName;
            string lastName;
            string newPassword;

            if (cmdparams.Length < 3)
                firstName = MainConsole.Instance.CmdPrompt("First name");
            else firstName = cmdparams[2];

            if (cmdparams.Length < 4)
                lastName = MainConsole.Instance.CmdPrompt("Last name");
            else lastName = cmdparams[3];

            if (cmdparams.Length < 5)
                newPassword = MainConsole.Instance.PasswdPrompt("New password");
            else newPassword = cmdparams[4];

            m_userDataBaseService.ResetUserPassword(firstName, lastName, newPassword);
        }

        /*
        private void HandleTestCommand(string module, string[] cmd)
        {
            m_log.Info("test command received");
        }
        */

        private void HandleLoginCommand(string module, string[] cmd)
        {
            string subcommand = cmd[1];

            switch (subcommand)
            {
                case "level":
                    // Set the minimal level to allow login 
                    // Useful to allow grid update without worrying about users.
                    // or fixing critical issues
                    //
                    if (cmd.Length > 2)
                    {
                        int level = Convert.ToInt32(cmd[2]);
                        m_loginService.setloginlevel(level);
                    }
                    break;
                case "reset":
                    m_loginService.setloginlevel(0);
                    break;
                case "text":
                    if (cmd.Length > 2)
                    {
                        m_loginService.setwelcometext(cmd[2]);
                    }
                    break;
            }
        }

        private void HandleDefault(string module, string[] cmd)
        {
            List<string> args = new List<string>(cmd);
            args.RemoveAt(0);
            string[] cmdparams = args.ToArray();

            if (cmdparams.Length > 0)
            {
                if (cmdparams[0].ToLower() == "regions")
                {
                    UserLoginService loginService;
                    if (m_core.TryGet<UserLoginService>(out loginService))
                    {
                        string filename = cmdparams.Length > 1 ? cmdparams[1] : String.Empty;
                        m_loginService = loginService;
                        m_loginService.LoadDefaultRegionsFromFile(filename);
                    }
                }
                if (cmdparams[0].ToLower() == "logins")
                {
                    UserLoginService loginService;
                    if (m_core.TryGet<UserLoginService>(out loginService))
                    {
                        string filename = cmdparams.Length > 1 ? cmdparams[1] : String.Empty;
                        m_loginService = loginService;
                        m_loginService.LoadDefaultLoginsFromFile(filename);
                    }
                }
            }
        }

        private void HandleTrustReload(string module, string[] args)
        {
            TrustManager.Instance.ReloadTrustLists();
        }

        public void RunCommand(string module, string[] cmd)
        {
            List<string> args = new List<string>(cmd);
            string command = cmd[0];

            args.RemoveAt(0);

            string[] cmdparams = args.ToArray();

            switch (command)
            {
                case "create":
                    do_create(cmdparams);
                    break;

                case "reset":
                    Reset(cmdparams);
                    break;


                case "test-inventory":
                    //  RestObjectPosterResponse<List<InventoryFolderBase>> requester = new RestObjectPosterResponse<List<InventoryFolderBase>>();
                    // requester.ReturnResponseVal = TestResponse;
                    // requester.BeginPostObject<UUID>(m_userManager._config.InventoryUrl + "RootFolders/", m_lastCreatedUser);
                    SynchronousRestObjectPoster.BeginPostObject<UUID, List<InventoryFolderBase>>(
                        "POST", m_cfg.InventoryUrl + "RootFolders/", m_lastCreatedUser);
                    break;

                case "logoff-user":
                    if (cmdparams.Length >= 3)
                    {
                        string firstname = cmdparams[0];
                        string lastname = cmdparams[1];
                        string message = String.Empty;

                        for (int i = 2; i < cmdparams.Length; i++)
                            message += " " + cmdparams[i];

                        UserProfileData theUser = null;
                        try
                        {
                            theUser = m_loginService.GetTheUser(firstname, lastname);
                        }
                        catch (Exception)
                        {
                            m_log.Error("[LOGOFF]: Error getting user data from the database.");
                        }

                        if (theUser != null)
                        {
                            if (theUser.CurrentAgent != null)
                            {
                                if (theUser.CurrentAgent.AgentOnline)
                                {
                                    m_log.Info("[LOGOFF]: Logging off requested user!");
                                    m_loginService.LogOffUser(theUser, message);

                                    theUser.CurrentAgent.AgentOnline = false;
                                    theUser.CurrentAgent.LogoutTime = Util.UnixTimeSinceEpoch();

                                    m_loginService.CommitAgent(ref theUser);
                                }
                                else
                                {
                                    m_log.Info(
                                        "[LOGOFF]: User Doesn't appear to be online, sending the logoff message anyway.");
                                    m_loginService.LogOffUser(theUser, message);

                                    theUser.CurrentAgent.AgentOnline = false;

                                    m_loginService.CommitAgent(ref theUser);
                                }
                            }
                            else
                            {
                                m_log.Error(
                                    "[LOGOFF]: Unable to logoff-user.  User doesn't have an agent record so I can't find the simulator to notify");
                            }
                        }
                        else
                        {
                            m_log.Info("[LOGOFF]: User doesn't exist in the database");
                        }
                    }
                    else
                    {
                        m_log.Error(
                            "[LOGOFF]: Invalid amount of parameters.  logoff-user takes at least three.  Firstname, Lastname, and message");
                    }

                    break;
            }
        }
    }
        #endregion
}
