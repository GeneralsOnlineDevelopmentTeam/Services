/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GenOnlineService
{
    // Logger for types without constructor injection.
    public static class AppLog
    {
        public static ILogger For(Type category)
        {
            return new DeferredLogger(category.FullName ?? category.Name);
        }

        public static ILogger For<T>()
        {
            return For(typeof(T));
        }

        // static fields can initialize before ServiceLocator.Services is set, so resolve on first use
        private sealed class DeferredLogger : ILogger
        {
            private readonly string _category;
            private ILogger? _logger;

            public DeferredLogger(string category)
            {
                _category = category;
            }

            private ILogger Inner
            {
                get
                {
                    if (_logger != null)
                    {
                        return _logger;
                    }

                    ILoggerFactory? factory = ServiceLocator.Services?.GetService<ILoggerFactory>();
                    return factory == null ? NullLogger.Instance : (_logger = factory.CreateLogger(_category));
                }
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
