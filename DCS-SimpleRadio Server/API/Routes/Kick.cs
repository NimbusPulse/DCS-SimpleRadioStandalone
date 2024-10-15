﻿using NLog;
using System.Net;
using System.Text.RegularExpressions;

namespace Ciribob.DCS.SimpleRadio.Standalone.Server.API.Routes
{
    internal class Kick
    {
        public static Response Handle(HttpListenerContext context, Logger logger)
        {
            string path = context.Request.Url.AbsolutePath;

            Match match = Regex.Match(path, @"^/ban/([a-zA-Z0-9]+)$");

            if (match.Success)
            {
                string userId = match.Groups[1].Value;
                logger.Info($"Received kick request for user ID: {userId}");

                return new Response(200, $"Kick instruction received for user ID: {userId}");
            }

            return new Response(400, "Invalid kick request format");
        }
    }
}
