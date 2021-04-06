using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using System.Diagnostics;

// Quick'n'Dirty Hack - based on: http://msdn.microsoft.com/en-GB/library/system.io.filesystemwatcher.changed.aspx

namespace when_changed
{
    class Program
    {
        private static string[] m_command_args;
        private static string m_command;

        private static State m_state;
        private static Object m_state_lock = new Object();


        public static void Main()
        {
            Run();
        }

        public static void Run()
        {
            string[] args = System.Environment.GetCommandLineArgs();

            // (First arg is the program path)

            if (args.Length < 3)
            {
                // Display the proper way to call the program.
                Console.WriteLine("Usage: when_changed (file path) (command) (optional-parameters)");
                return;
            }

            String thingToWatch = args[1];
            FileSystemWatcher watcher = createWatcher(thingToWatch);

            m_command = args[2];
            m_command_args = args.Skip(3).ToArray();

            // Add event handlers.
            watcher.Changed += new FileSystemEventHandler(OnChanged);
            watcher.Created += new FileSystemEventHandler(OnChanged);
            watcher.Deleted += new FileSystemEventHandler(OnChanged);
            watcher.Renamed += new RenamedEventHandler(OnRenamed);

            // Begin watching.
            watcher.EnableRaisingEvents = true;

            // Wait for the user to quit the program.
            Console.WriteLine("when_changed now watching: " + watcher.Path + "\\" + watcher.Filter);

            Console.WriteLine("Ctrl-C to quit.");

            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.F)
                {
                    Console.WriteLine("Forcing run...");
                    runCmd("");
                }
            }
        }

        public static FileSystemWatcher createWatcher(String thingToWatch)
        {
            // Two things are determined from the argument:
            String dirToWatch; // The directory to watch.
            String fileFilter; // The filter for which files in that directory to watch.

            if (!thingToWatch.Contains(Path.DirectorySeparatorChar))
            {

                dirToWatch = Directory.GetCurrentDirectory();
                fileFilter = Path.GetFileName(thingToWatch);
            }
            else
            {
                dirToWatch = Path.GetDirectoryName(thingToWatch);
                fileFilter = Path.GetFileName(thingToWatch);
            }

            // Create a new FileSystemWatcher and set its properties.
            FileSystemWatcher watcher = new FileSystemWatcher();
            watcher.Path = dirToWatch;
            /* Watch for changes in LastAccess and LastWrite times, and
               the renaming of files or directories. */
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;


            watcher.Filter = fileFilter;
            watcher.IncludeSubdirectories = fileFilter.Contains("**");
            return watcher;
        }


        // Define the event handlers.
        private static void OnChanged(object source, FileSystemEventArgs e)
        {
            // Specify what is done when a file is changed, created, or deleted.
            Console.WriteLine(DateTime.Now.ToShortTimeString() + " File: " + e.FullPath + " " + e.ChangeType);
            runCmd(e.FullPath);
        }

        private static void OnRenamed(object source, RenamedEventArgs e)
        {
            // Specify what is done when a file is renamed.
            Console.WriteLine(DateTime.Now.ToShortTimeString() + "File: {0} renamed to {1}.", e.OldFullPath, e.FullPath);
            runCmd(e.FullPath);
        }

        private static void runCmd(string changed_file)
        {
            // When a file is updated, we often get a flurry of updates in a single second.
            lock (m_state_lock)
            {
                switch (m_state)
                {
                    case State.Executing:
                        // Oh noeeees - it changed while we were executing. do it again straight after.
                        Console.WriteLine(" -- output will be dirty - will run again soon...");
                        m_state = State.ExecutingDirty;
                        break;
                    case State.ExecutingDirty:
                        // Leave the flag dirty.
                        break;
                    case State.WaitingToExecute:
                        break;
                    case State.Watching:
                        // Start a new thread to delay and run the command, meanwhile subsequent nots. ignored.
                        m_state = State.WaitingToExecute;
                        Thread t = new Thread(new ParameterizedThreadStart(threadRun));
                        t.Start(changed_file);
                        break;
                    default:
                        throw new InvalidProgramException("argh! enum values?!");
                }
            }

        }

        private static void threadRun(object changed_file)
        {
            string changedfile = (string)changed_file;
            Boolean again = true;
            while (again)
            {
                waitThenRun(changedfile);

                // When a file is updated, we often get a flurry of updates in a single second.
                lock (m_state_lock)
                {
                    switch (m_state)
                    {
                        case State.Executing:
                            // no subsequent changes - output ok (ish)
                            m_state = State.Watching;
                            again = false;
                            break;
                        case State.ExecutingDirty:
                            // Clean the dirty flag, and repeat.
                            m_state = State.WaitingToExecute;
                            again = true;
                            break;
                        case State.WaitingToExecute:
                            throw new InvalidProgramException("shouldn't happen");
                        case State.Watching:
                            throw new InvalidProgramException("shouldn't happen");
                        default:
                            throw new InvalidProgramException("argh! enum values?!");
                    }
                }
            }
        }

        private static void waitThenRun(string filechanged)
        {
            // TODO: original comment didn't explain
            // What issues does this cause when removed? Some other semaphore|drop is probably better.
            Thread.Sleep(100);

            // Substitute the lexical token in args, for its file target
            var modifiedArgs = string.Join(" ", from arg in m_command_args
                                                select arg == "%file%" ? filechanged : arg);

            // Start the execution.
            lock (m_state_lock)
            {
                Debug.Assert(m_state == State.WaitingToExecute);
                m_state = State.Executing;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo(m_command, modifiedArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Directory.GetCurrentDirectory(),
            };
            using (Process task = new Process())
            {
                task.StartInfo = startInfo;
                // Don't print (additional) empty lines
                task.OutputDataReceived += (sender, args) => { if (args.Data?.Length > 0) Console.WriteLine(args.Data); };
                task.ErrorDataReceived += (sender, args) => { if (args.Data?.Length > 0) Console.Error.WriteLine(args.Data); };

                task.Start();

                task.BeginOutputReadLine();
                task.BeginErrorReadLine();
                task.WaitForExit();

                Console.WriteLine();
            }
        }
    }
}
