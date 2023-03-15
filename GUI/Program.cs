/*
    Copyright 2010 MCSharp team (Modified for use with MCZall/MCLawl/SuperNova)
    
    Dual-licensed under the Educational Community License, Version 2.0 and
    the GNU General Public License, Version 3 (the "Licenses"); you may
    not use this file except in compliance with the Licenses. You may
    obtain a copy of the Licenses at
    
    http://www.opensource.org/licenses/ecl2.php
    http://www.gnu.org/licenses/gpl-3.0.html
    
    Unless required by applicable law or agreed to in writing,
    software distributed under the Licenses are distributed on an "AS IS"
    BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express
    or implied. See the Licenses for the specific language governing
    permissions and limitations under the Licenses.
 */
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SuperNova.Gui 
{
    public static class Program 
    {   
        [STAThread]
        public static void Main(string[] args) {
            Environment.CurrentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            
            if (!File.Exists("SuperNova_.dll")) {
                Popup.Error("Cannot start server as SuperNova_.dll is missing from " + Environment.CurrentDirectory 
                            + "\r\nDownload it from " + Updater.UploadsURL);
                return;
            }
            // separate method, in case SuperNova_.dll is missing
            StartGUI();
        }
        
        static void StartGUI() {
            FileLogger.Init();
            Server.RestartPath = Application.ExecutablePath;
            AppDomain.CurrentDomain.UnhandledException += GlobalExHandler;
            Application.ThreadException += ThreadExHandler;

            try {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault( false );
                Application.Run(new Window());
            } catch (Exception e) {
                Logger.LogError(e);
            }
        }
        
        static void LogAndRestart(Exception ex) {
            Logger.LogError(ex);
            FileLogger.Flush(null);
            
            Thread.Sleep(500);
            if (Server.Config.restartOnError) {
                Thread stopThread = Server.Stop(true, "Server restart - unhandled error");
                stopThread.Join();
            }
        }
        
        static void GlobalExHandler(object sender, UnhandledExceptionEventArgs e) {
            LogAndRestart((Exception)e.ExceptionObject);
        }

        static void ThreadExHandler(object sender, ThreadExceptionEventArgs e) {
            LogAndRestart(e.Exception);
        }
    }
}

