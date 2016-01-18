using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using StackExchange.Exceptional.Stores;
using StackExchange.Exceptional.Extensions;
#if COREFX
using Microsoft.Extensions.PlatformAbstractions;
#else
using System.Configuration;
using System.IO;
using System.Transactions;
using StackExchange.Exceptional.Email;
#endif

namespace StackExchange.Exceptional
{
    /// <summary>
    /// Represents an error log capable of storing and retrieving errors generated in an ASP.NET Web application.
    /// </summary>
    public abstract partial class ErrorStore
    {
        private static ErrorStore _defaultStore;
        
        [ThreadStatic]
        private static List<Regex> _ignoreRegexes;
        [ThreadStatic]
        private static List<string> _ignoreExceptions;

        private static ConcurrentQueue<Error> _writeQueue;

        private static bool _enableLogging = true;
        private static Thread _retryThread;
        private static readonly object _retryLock = new object();
        // TODO: possibly make this configurable
        internal const int _retryDelayMiliseconds = 2000;
        private static bool _isInRetry;
        private static Exception _retryException;
        internal const string CustomDataErrorKey = "CustomDataFetchError";

        /// <summary>
        /// The default number of exceptions (rollups count as 1) to buffer in memory in the event of an error store outage
        /// </summary>
        public const int DefaultBackupQueueSize = 1000;
        /// <summary>
        /// The default number of seconds to roll up errors for.  Identical stack trace errors within 10 minutes get a DuplicateCount++ instead of a separate exception logged.
        /// </summary>
        public const int DefaultRollupSeconds = 600;

        /// <summary>
        /// Base constructor of the error store to set common properties
        /// </summary>
        protected ErrorStore(ErrorStoreSettings settings) : this(settings.RollupSeconds, settings.BackupQueueSize) {}

        /// <summary>
        /// Creates an error store with the specified rollup
        /// </summary>
        protected ErrorStore(int rollupSeconds, int backupQueueSize = DefaultBackupQueueSize)
        {
            if (rollupSeconds > 0)
                RollupThreshold = TimeSpan.FromSeconds(rollupSeconds);

            BackupQueueSize = backupQueueSize > 0
                ? backupQueueSize
                : DefaultBackupQueueSize;
        }

        /// <summary>
        /// The size of the backup/retry queue for logging, defaults to 1000
        /// </summary>
        public int BackupQueueSize { get; set; }

        /// <summary>
        /// Gets if this error store is 
        /// </summary>
        public bool InFailureMode => _isInRetry;

        /// <summary>
        /// The Rollup threshold within which errors logged rapidly are rolled up
        /// </summary>
        protected TimeSpan? RollupThreshold;

        /// <summary>
        /// The last time this error store failed to write an error
        /// </summary>
        protected DateTime? LastWriteFailure;

        /// <summary>
        /// Logs an error in log for the application
        /// </summary>
        protected abstract void LogError(Error error);

        /// <summary>
        /// Retrieves a single error based on Id
        /// </summary>
        protected abstract Error GetError(Guid guid);

        /// <summary>
        /// Prevents error identfied by 'id' from being deleted when the error log is full, if the store supports it
        /// </summary>
        protected abstract bool ProtectError(Guid guid);

        /// <summary>
        /// Protects a list of errors in the log
        /// </summary>
        protected virtual bool ProtectErrors(IEnumerable<Guid> guids)
        {
            var success = true;
            foreach (var guid in guids)
                if (!ProtectError(guid))
                    success = false;
            return success;
        }

        /// <summary>
        /// Deletes a specific error from the log
        /// </summary>
        protected abstract bool DeleteError(Guid guid);

        /// <summary>
        /// Deletes a list of errors from the log, only if they are not protected
        /// </summary>
        protected virtual bool DeleteErrors(IEnumerable<Guid> guids)
        {
            var success = true;
            foreach (var guid in guids)
                if (!DeleteError(guid))
                    success = false;
            return success;
        }

        /// <summary>
        /// Deletes a specific error from the log, any traces of it
        /// </summary>
        protected virtual bool HardDeleteError(Guid guid) { return DeleteError(guid); }

        /// <summary>
        /// Deletes all non-protected errors from the log
        /// </summary>
        protected abstract bool DeleteAllErrors(string applicationName = null);

        /// <summary>
        /// Retrieves all of the errors in the log
        /// </summary>
        protected abstract int GetAllErrors(List<Error> list, string applicationName = null);

        /// <summary>
        /// Retrieves a count of application errors since the specified date, or all time if null
        /// </summary>
        protected abstract int GetErrorCount(DateTime? since = null, string applicationName = null);

        /// <summary>
        /// Get the name of this error log store implementation.
        /// </summary>
        public virtual string Name => GetType().Name;

        /// <summary>
        /// Gets the name of the application to which the log is scoped.
        /// </summary>
        public static string ApplicationName { get; private set; } = Settings.Current.ApplicationName;

        /// <summary>
        /// Gets the name of the machine logging these errors.
        /// </summary>
        public virtual string MachineName =>
#if COREFX
            // TODO: RC2 Fix to .MachineName
            Environment.GetEnvironmentVariable("HOSTNAME");
#else
            Environment.MachineName;
#endif

        /// <summary>
        /// Gets the list of exceptions to ignore specified in the configuration file
        /// </summary>
        public static List<Regex> IgnoreRegexes
        {
            get { return _ignoreRegexes ?? (_ignoreRegexes = Settings.Current.IgnoreErrors.Regexes.All().Select(r => r.PatternRegex).ToList()); }
        }

        /// <summary>
        /// Gets the list of exceptions to ignore specified in the configuration file
        /// </summary>
        public static List<string> IgnoreExceptions
        {
            get { return _ignoreExceptions ?? (_ignoreExceptions = Settings.Current.IgnoreErrors.Types.All().Select(r => r.Type).ToList()); }
        }

        /// <summary>
        /// Gets the default error store specified in the configuration, 
        /// or the in-memory store if none is configured.
        /// </summary>
        public static ErrorStore Default => _defaultStore ?? (_defaultStore = GetErrorStoreFromConfig());

        /// <summary>
        /// Sets the default error store to use for logging
        /// </summary>
        /// <param name="applicationName">The application name to use when logging errors</param>
        /// <param name="store">The error store used to store, e.g. <code>new SQLErrorStore(myConnectionString)</code></param>
        public static void Setup(string applicationName, ErrorStore store)
        {
            _defaultStore = store;
            ApplicationName = applicationName;
        }

        /// <summary>
        /// Gets the write queue for errors, which is populated in the case of a write failure
        /// </summary>
        public static ConcurrentQueue<Error> WriteQueue => _writeQueue ?? (_writeQueue = new ConcurrentQueue<Error>());

        /// <summary>
        /// Gets the last exception that happened when trying to log exceptions
        /// </summary>
        public static Exception LastRetryException => _retryException;

        /// <summary>
        /// Logs an error in log for the application
        /// </summary>
        public void Log(Error error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));

            // Track the GUID we made vs. what the store returns. If it's different, it's a dupe.
            var originalGuid = error.GUID;
            // if we're in a retry state, log directly to the queue
            if (_isInRetry)
            {
                QueueError(error);
                if (originalGuid != error.GUID) error.IsDuplicate = true;
#if !COREFX
                ErrorEmailer.SendMail(error);
#endif
                return;
            }
            try
            {
#if !COREFX
                using (new TransactionScope(TransactionScopeOption.Suppress))
#endif
                {
                    LogError(error);
                }
                if (originalGuid != error.GUID) error.IsDuplicate = true;
#if !COREFX
                ErrorEmailer.SendMail(error);
#endif
            }
            catch (Exception ex)
            {
                _retryException = ex;
                // if we fail to write the error to the store, queue it for re-writing
                QueueError(error);
            }
        }

        /// <summary>
        /// Deletes all non-protected errors from the log
        /// </summary>
        public bool Protect(Guid guid)
        {
            if (_isInRetry) return false; // no protecting allowed when failing, since we don't respect it in the queue anyway

#if !COREFX
            using (new TransactionScope(TransactionScopeOption.Suppress))
#endif
            {
                return ProtectError(guid);
            }
        }

        /// <summary>
        /// Protects a list of errors in the log
        /// </summary>
        public bool ProtectList(IEnumerable<Guid> guids)
        {
            if (_isInRetry) return false; // no protecting allowed when failing, since we don't respect it in the queue anyway

            try
            {
#if !COREFX
                using (new TransactionScope(TransactionScopeOption.Suppress))
#endif
                {
                    return ProtectErrors(guids);
                }
            }
            catch (Exception ex)
            {
                BeginRetry(ex);
                return false;
            }
        }

        /// <summary>
        /// Deletes an error from the log with the specified id
        /// </summary>
        public bool Delete(Guid guid)
        {
            if (_isInRetry) return false; // no deleting from the retry queue

            try
            {
#if !COREFX
                using (new TransactionScope(TransactionScopeOption.Suppress))
#endif
                {
                    return DeleteError(guid);
                }
            }
            catch (Exception ex)
            {
                BeginRetry(ex);
                return false;
            }
        }

        /// <summary>
        /// Deletes a list of non-protected errors from the log
        /// </summary>
        public bool DeleteList(IEnumerable<Guid> guids)
        {
            if (_isInRetry) return false; // no deleting from the retry queue

            try
            {
#if !COREFX
                using (new TransactionScope(TransactionScopeOption.Suppress))
#endif
                {
                    return DeleteErrors(guids);
                }
            }
            catch (Exception ex)
            {
                BeginRetry(ex);
                return false;
            }
        }

        /// <summary>
        /// Deletes all non-protected errors from the log
        /// </summary>
        public bool DeleteAll(string applicationName = null)
        {
            if (_isInRetry)
            {
                _writeQueue = new ConcurrentQueue<Error>();
                return true;
            }

            try
            {
#if !COREFX
                using (new TransactionScope(TransactionScopeOption.Suppress))
#endif
                {
                    return DeleteAllErrors(applicationName);
                }
            }
            catch (Exception ex)
            {
                BeginRetry(ex);
                return false;
            }
        }

        /// <summary>
        /// Gets a specific exception with the specified guid
        /// </summary>
        public Error Get(Guid guid)
        {
            if (_isInRetry)
            {
                return WriteQueue.FirstOrDefault(e => e.GUID == guid);
            }

            try { return GetError(guid); }
            catch (Exception ex) { BeginRetry(ex); }
            return null;
        }

        /// <summary>
        /// Gets all in the store, including those in the backup queue if it's in use
        /// </summary>
        public int GetAll(List<Error> errors, string applicationName = null)
        {
            if (_isInRetry)
            {
                errors.AddRange(WriteQueue);
                return errors.Count;
            }

            try { return GetAllErrors(errors, applicationName); }
            catch (Exception ex) { BeginRetry(ex); }
            return 0;
        }

        /// <summary>
        /// Gets the count of exceptions, optionally those since a certain date
        /// </summary>
        public int GetCount(DateTime? since = null, string applicationName = null)
        {
            if (_isInRetry)
            {
                return WriteQueue.Count;
            }

            try { return GetErrorCount(since, applicationName); }
            catch (Exception ex) { BeginRetry(ex); }
            return 0;
        }

        /// <summary>
        /// Queues an error into the backup/retry queue
        /// </summary>
        /// <remarks>These will be written to the store when we're able to connect again</remarks>
        public void QueueError(Error e)
        {
            // try and rollup in the queue, to save space
            foreach (var err in WriteQueue.Where(err => e.ErrorHash == err.ErrorHash))
            {
                e.GUID = err.GUID;
                err.DuplicateCount++;
                return;
            }

            // only queue if we're under the cap
            if (WriteQueue.Count < BackupQueueSize)
                WriteQueue.Enqueue(e);

            // spin up the retry mechanism
            BeginRetry();
        }

        private static void BeginRetry(Exception ex = null)
        {
            lock (_retryLock)
            {
                if (ex != null) _retryException = ex;
                _isInRetry = true;

                // are we already spun up?
                if (_retryThread != null && _retryThread.IsAlive) return;

                _retryThread = new Thread(TryFlushQueue);
                _retryThread.Start();
            }
        }

        private static void TryFlushQueue()
        {
            if (!_isInRetry && WriteQueue.IsEmpty) return;

            while (true)
            {
                Thread.Sleep(_retryDelayMiliseconds);

                // if the error store is still down, sleep again
                if (!Default.Test()) continue;

                // empty queue
                while (!WriteQueue.IsEmpty)
                {
                    Error e;
                    // if we can't pop one off, get out of here
                    if (!WriteQueue.TryDequeue(out e)) return;

                    try
                    {
                        Default.LogError(e);
                    }
                    catch
                    {
                        // if we had an error logging, stick it back in the queue and jump out, else we'll iterate this thing forever
                        Default.QueueError(e);
                        break;
                    }
                }
                // if we emptied the queue, return to a normal state
                if (WriteQueue.IsEmpty)
                {
                    _isInRetry = false;
                    TryFlushQueue(); // clear out any that may have come in due to thread races
                    return;
                }
            }
        }

        /// <summary>
        /// Tests to see if this error store is working
        /// </summary>
        public bool Test()
        {
            try
            {
                var error = new Error(new Exception("Test Exception"));
                LogError(error);
                HardDeleteError(error.GUID);
                return true;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return false;
            }
        }

        private static ErrorStore GetErrorStoreFromConfig()
        {
            return GetFromSettings(Settings.Current.ErrorStore) ?? new MemoryErrorStore();
        }

        private static ErrorStore GetFromSettings(ErrorStoreSettings settings)
        {
            if (settings == null) return null;

            // a bit of validation
            if (settings.Type.IsNullOrEmpty())
                throw new ArgumentOutOfRangeException(nameof(settings), "ErrorStore 'type' must be specified");
            if (settings.Size < 1) 
                throw new ArgumentOutOfRangeException(nameof(settings),"ErrorStore 'size' must be positive");

            var storeTypes = GetErrorStores();
            // Search by convention first
            var match = storeTypes.FirstOrDefault(s => s.Name == settings.Type + "ErrorStore")
                        // well shit, free for all!
                        ?? storeTypes.FirstOrDefault(s => s.Name.Contains(settings.Type));

            if (match == null)
            {
                throw new Exception("Could not find error store type: " + settings.Type);
            }

            try
            {
                return (ErrorStore) Activator.CreateInstance(match, settings);
            }
            catch (Exception ex)
            {
                throw new Exception("Error creating a " + settings.Type + " error store: " + ex.Message, ex);
            }
        }

        private static List<Type> GetErrorStores()
        {
            var result = new List<Type>();
            // It's intentional even the core error stores load the same way, as a sanity check
            // We're loading all assemblies referencing StackExchange.Exceptional and loading their types as well
#if COREFX
            var libs = PlatformServices.Default.LibraryManager.GetReferencingLibraries("StackExchange.Exceptional")
                .SelectMany(info => info.Assemblies)
                .Select(info => Assembly.Load(new AssemblyName(info.Name)));
#else
            var assemblyPath = Assembly.GetExecutingAssembly().GetName().CodeBase;
            Uri assemblyUri = new Uri(Path.GetDirectoryName(assemblyPath));
            if (string.IsNullOrEmpty(assemblyUri.LocalPath))
            {
                Debug.WriteLine("Error loading Error stores, abs path: " + assemblyUri.AbsolutePath);
                return result;
            }

            var libs = Directory.GetFiles(assemblyUri.LocalPath, "StackExchange.Exceptional*.dll")
                    .Select(Assembly.LoadFrom);
#endif
            try
            {
                foreach (var library in libs)
                {
                    try
                    {   
                        result.AddRange(library.GetTypes().Where(type => type
#if COREFX
                        .GetTypeInfo()
#endif
                        .IsSubclassOf(typeof (ErrorStore))));
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"Error loading ErrorStore types from {library.FullName}: {e.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading error stores: " + ex.Message);
            }
            return result;
        }
        
        public bool ShouldIgnore(Exception ex)
        {
            if (IgnoreRegexes.Any(re => re.IsMatch(ex.ToString())))
                return true;
            if (IgnoreExceptions.Any(type => IsDescendentOf(ex.GetType(), type.ToString())))
                return true;
            return false;
        }

        /// <summary>
        /// Logs an exception to the configured error store, or the in-memory default store if none is configured
        /// </summary>
        /// <param name="ex">The exception to log</param>
        /// <param name="appendFullStackTrace">Whether to append a full stack trace to the exception's detail</param>
        /// <param name="rollupPerServer">Whether to log up per-server, e.g. errors are only duplicates if they have same stack on the same machine</param>
        /// <param name="customData">Any custom data to store with the exception like UserId, etc...this will be rendered as JSON in the error view for script use</param>
        /// <param name="applicationName">If specified, the application name to log with, if not specified the name in the config is used</param>
        /// <param name="setProperties">Allows downstream providers to set properties, e.g. from a platform's HttpContext.</param>
        /// <returns>The Error created, if one was created and logged, null if nothing was logged</returns>
        /// <remarks>
        /// When dealing with a non web requests, pass <see langword="null" /> in for context.  
        /// It shouldn't be forgotten for most web application usages, so it's not an optional parameter.
        /// </remarks>
        public Error Log(Exception ex, 
            bool appendFullStackTrace = false, 
            bool rollupPerServer = false, 
            Dictionary<string, string> customData = null, 
            string applicationName = null,
            Action<Error> setProperties = null)
        {
            if (!_enableLogging) return null;
            try
            {
                if (Default.ShouldIgnore(ex))
                    return null;

                if (customData == null && GetCustomData != null)
                {
                    customData = new Dictionary<string, string>();
                    try
                    {
                        GetCustomData(ex, customData);
                    }
                    catch (Exception cde)
                    {
                        // if there was an error getting custom errors, log it so we can display such in the view...and not fail to log the original error
                        customData.Add(CustomDataErrorKey, cde.ToString());
                    }
                }

                var error = new Error(ex, applicationName)
                                {
                                    RollupPerServer = rollupPerServer,
                                    CustomData = customData ?? new Dictionary<string, string>()
                                };
                setProperties?.Invoke(error);

                // Globally apply logging filters
                error.ApplyLoggingFilters();

                if (GetIPAddress != null)
                {
                    try
                    {
                        error.IPAddress = GetIPAddress();
                    }
                    catch (Exception gipe)
                    {
                        // if there was an error getting the IP, log it so we can display such in the view...and not fail to log the original error
                        error.CustomData.Add(CustomDataErrorKey, "Fetching IP Adddress: " + gipe);
                    }
                }

                var exCursor = ex;
                while (exCursor != null)
                {
                    error.AddFromData(exCursor);
                    exCursor = exCursor.InnerException;
                }

                if (appendFullStackTrace)
                {
                    var frames = new StackTrace(ex, true).GetFrames();
                    if (frames != null && frames.Length > 2)
                        error.Detail += "\n\nFull Trace:\n\n" + string.Join("", frames.Skip(2));
                    error.ErrorHash = error.GetHash();
                }

                if (OnBeforeLog != null)
                {
                    try
                    {
                        var args = new ErrorBeforeLogEventArgs(error);
                        OnBeforeLog(Default, args);
                        if (args.Abort) return null; // if we've been told to abort, then abort dammit!
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e);
                    }
                }

                Debug.WriteLine(ex); // always echo the error to trace for local debugging
                Default.Log(error);

                if (OnAfterLog != null)
                {
                    try
                    {
                        OnAfterLog(Default, new ErrorAfterLogEventArgs(error));
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e);
                    }
                }
                return error;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return null;
            }
        }

        /// <summary>
        /// Returns true if t is of className, or descendent from className
        /// </summary>
        private static bool IsDescendentOf(Type t, string className)
        {
            if (t.FullName == className) return true;
#if COREFX
            var bt = t.GetTypeInfo().BaseType;
#else
            var bt = t.BaseType;
#endif
            return bt != null && IsDescendentOf(bt, className);
        }

#if !COREFX
        /// <summary>
        /// Gets the connection string from the connectionStrings configuration element, from web.config or app.config, throws if not found.
        /// </summary>
        /// <param name="connectionStringName">The connection string name to fetch</param>
        /// <returns>The connection string requested</returns>
        /// <exception cref="ConfigurationErrorsException">Connection string was not found</exception>
        protected static string GetConnectionStringByName(string connectionStringName)
        {
            if (connectionStringName.IsNullOrEmpty()) return null;

            var connectionString = ConfigurationManager.ConnectionStrings[connectionStringName];
            if (connectionString == null)
                throw new ConfigurationErrorsException("A connection string was not found for the connection string name provided");
            return connectionString.ConnectionString;
        }
#endif
    }
}