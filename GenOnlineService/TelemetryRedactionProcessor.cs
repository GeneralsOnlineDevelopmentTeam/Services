/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
*/

using System.Diagnostics;
using OpenTelemetry;

namespace GenOnlineService
{
    // Removes SQL, connection details and outbound URL paths (the TURN key is in the path) from spans before export.
    public sealed class TelemetryRedactionProcessor : BaseProcessor<Activity>
    {
        private static readonly string[] s_databaseTags =
        [
            "db.statement",
            "db.query.text",
            "db.connection_string",
            "db.user",
            // derived from the connection string
            "db.npgsql.data_source"
        ];

        public override void OnEnd(Activity activity)
        {
            if (activity.Source.Name is AppMetrics.MySqlActivitySourceName or AppMetrics.NpgsqlActivitySourceName)
            {
                foreach (string tag in s_databaseTags)
                {
                    activity.SetTag(tag, null);
                }
            }

            if (activity.Kind == ActivityKind.Client)
            {
                RedactUrl(activity, "url.full");
                RedactUrl(activity, "http.url");
                activity.SetTag("url.path", null);
                activity.SetTag("url.query", null);
            }
        }

        private static void RedactUrl(Activity activity, string tag)
        {
            if (activity.GetTagItem(tag) is not string url)
            {
                return;
            }

            activity.SetTag(tag, Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.GetLeftPart(UriPartial.Authority) : null);
        }
    }
}
