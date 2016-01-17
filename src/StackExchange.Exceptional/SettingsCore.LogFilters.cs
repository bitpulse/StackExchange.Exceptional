﻿#if COREFX
using System.Collections.Generic;

namespace StackExchange.Exceptional
{
    public partial class Settings
    {
        /// <summary>
        /// The Ignore section of the configuration, optional and no errors will be blocked from logging if not specified
        /// </summary>
        public LogFilterSettings LogFilters { get; set; }

        /// <summary>
        /// Ignore element for deserilization from a configuration, e.g. web.config or app.config
        /// </summary>
        public class LogFilterSettings
        {
            /// <summary>
            /// Form submitted values to replace on save - this prevents logging passwords, etc.
            /// </summary>
            public List<LogFilter> FormFilters { get; set; }

            /// <summary>
            /// Cookie values to replace on save - this prevents logging auth tokens, etc.
            /// </summary>
            public List<LogFilter> CookieFilters { get; set; }
        }
    }

    /// <summary>
    /// A filter entry with the forn variable name and what to replace the value with when logging
    /// </summary>
    public class LogFilter
    {
        /// <summary>
        /// The form parameter name to ignore
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The value to log instead of the real value
        /// </summary>
        public string ReplaceWith { get; set; }
    }
}
#endif