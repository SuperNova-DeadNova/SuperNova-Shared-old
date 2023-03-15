﻿/*
    Copyright 2015 SuperNova
        
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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using SuperNova.Config;
using SuperNova.Network;

namespace SuperNova.Modules.Relay1.Discord1
{
    /// <summary> Represents an abstract Discord API message </summary>
    public abstract class DiscordApiMessage1
    {
        /// <summary> The path/route that will handle this message </summary>
        /// <example> /channels/{channel id}/messages </example>
        public string Path;
        /// <summary> The HTTP method to handle the path/route with </summary>
        /// <example> POST, PATCH, DELETE </example>
        public string Method = "POST";
        
        /// <summary> Converts this message into its JSON representation </summary>
        public abstract JsonObject ToJson();
        
        /// <summary> Attempts to combine this message with a prior message to reduce API calls </summary>
        public virtual bool CombineWith(DiscordApiMessage1 prior) { return false; }
    }
    
    /// <summary> Message for sending text to a channel </summary>
    public class ChannelSendMessage : DiscordApiMessage1
    {
        static JsonArray default_allowed = new JsonArray() { "users", "roles" };
        StringBuilder content;
        public JsonArray Allowed;
        
        public ChannelSendMessage(string channelID, string message) {
            Path    = "/channels/" + channelID + "/messages";
            content = new StringBuilder(message);
        }
        
        public override JsonObject ToJson() {
            // only allow pinging certain groups
            JsonObject allowed = new JsonObject()
            {
                { "parse", Allowed ?? default_allowed }
            };
            JsonObject obj = new JsonObject()
            {
                { "content", content.ToString() },
                { "allowed_mentions", allowed }
            };
            return obj;
        }
        
        public override bool CombineWith(DiscordApiMessage1 prior) {
            ChannelSendMessage msg = prior as ChannelSendMessage;
            if (msg == null || msg.Path != Path) return false;
            
            if (content.Length + msg.content.Length > 1024) return false;
            
            // TODO: is stringbuilder even beneficial here
            msg.content.Append('\n');
            msg.content.Append(content.ToString());
            content.Length = 0; // clear this
            return true;
        }
    }
    
    public class ChannelSendEmbed : DiscordApiMessage1
    {
        public string Title;
        public Dictionary<string, string> Fields = new Dictionary<string, string>();
        public int Color;
        
        public ChannelSendEmbed(string channelID) {
            Path = "/channels/" + channelID + "/messages";
        }
        
        JsonArray GetFields() {
            JsonArray arr = new JsonArray();
            foreach (var raw in Fields) 
            { 
                JsonObject field = new JsonObject()
                {
                    { "name",   raw.Key  },
                    { "value", raw.Value }
                };
                arr.Add(field);
            }
            return arr;
        }
        
        public override JsonObject ToJson() {
            JsonObject obj = new JsonObject()
            {
                { "embed", new JsonObject()
                    {
                        { "title", Title },
                        { "color", Color },
                        { "fields", GetFields() }
                    }
                },
                // no pinging anything
                { "allowed_mentions", new JsonObject()
                    {
                        { "parse", new JsonArray() }
                    }
                }
            };
            return obj;
        }
    }
    
    /// <summary> Implements a basic web client for sending messages to the Discord API </summary>
    /// <remarks> https://discord.com/developers/docs/reference </remarks>
    /// <remarks> https://discord.com/developers/docs/resources/channel#create-message </remarks>
    public sealed class DiscordApiClient1 : RelayBotSender1<DiscordApiMessage1>
    {
        public string Token;
        const string host = "https://discord.com/api/v8";
        
        DiscordApiMessage1 GetNextRequest() {
            if (requests.Count == 0) return null;
            DiscordApiMessage1 first = requests.Dequeue();
            
            // try to combine messages to minimise API calls
            while (requests.Count > 0) {
                DiscordApiMessage1 next = requests.Peek();
                if (!next.CombineWith(first)) break;
                requests.Dequeue();
            }
            return first;
        }
        
        protected override string ThreadName { get { return "Discord-ApiClient"; } }
        protected override void HandleNext() {
            DiscordApiMessage1 msg1 = null;
            WebResponse res = null;
            
            lock (reqLock)   { msg1 = GetNextRequest(); }
            if (msg1 == null) { WaitForWork(); return; }
            
            for (int retry = 0; retry < 10; retry++) 
            {
                try {
                    HttpWebRequest req = HttpUtil.CreateRequest(host + msg1.Path);
                    req.Method         = msg1.Method;
                    req.ContentType    = "application/json";
                    req.Headers[HttpRequestHeader.Authorization] = "Bot " + Token;
                    
                    string data = Json.SerialiseObject(msg1.ToJson());
                    HttpUtil.SetRequestData(req, Encoding.UTF8.GetBytes(data));
                    res = req.GetResponse();
                    
                    HttpUtil.GetResponseText(res);
                    break;
                } catch (WebException ex) {
                    string err = HttpUtil.GetErrorResponse(ex);
                    HttpUtil.DisposeErrorResponse(ex);
                    HttpStatusCode status = GetStatus(ex);
                    
                    // 429 errors simply require retrying after sleeping for a bit
                    if (status == (HttpStatusCode)429) {
                        SleepForRetryPeriod(ex.Response);
                        continue;
                    }
                    
                    // 500 errors might be temporary Discord outage, so still retry a few times
                    if (status >= (HttpStatusCode)500 && status <= (HttpStatusCode)504) {
                        LogWarning(ex);
                        LogResponse(err);
                        if (retry >= 2) return;
                        continue;
                    }
                    
                    // If unable to reach Discord at all, immediately give up
                    if (ex.Status == WebExceptionStatus.NameResolutionFailure) {
                        LogWarning(ex);
                        return;
                    }
                    
                    // May be caused by connection dropout/reset, so still retry a few times
                    if (ex.InnerException is IOException) {
                        LogWarning(ex);
                        if (retry >= 2) return;
                        continue;
                    }
                    
                    LogError(ex, msg1);
                    LogResponse(err);
                    return;
                } catch (Exception ex) {
                    LogError(ex, msg1);
                    return;
                }
            }
            
            // Avoid triggering HTTP 429 error if possible
            string remaining = res.Headers["X-RateLimit-Remaining"];
            if (remaining == "1") SleepForRetryPeriod(res);
        }
        
        
        static HttpStatusCode GetStatus(WebException ex) {
            if (ex.Response == null) return 0;            
            return ((HttpWebResponse)ex.Response).StatusCode;
        }
        
        static void LogError(Exception ex, DiscordApiMessage1 msg) {
            string target = "(" + msg.Method + " " + msg.Path + ")";
            Logger.LogError("Error sending request to Discord API " + target, ex);
        }
        
        static void LogWarning(Exception ex) {
            Logger.Log(LogType.Warning, "Error sending request to Discord API - " + ex.Message);
        }
        
        static void LogResponse(string err) {
            if (string.IsNullOrEmpty(err)) return;
            Logger.Log(LogType.Warning, "Discord API returned: " + err);
        }
        
        
        static void SleepForRetryPeriod(WebResponse res) {
            string resetAfter = res.Headers["X-RateLimit-Reset-After"];
            string retryAfter = res.Headers["Retry-After"];
            float delay;
            
            if (Utils.TryParseSingle(resetAfter, out delay) && delay > 0) {
                // Prefer Discord "X-RateLimit-Reset-After" (millisecond precision)
            } else if (Utils.TryParseSingle(retryAfter, out delay) && delay > 0) {
                // Fallback to general "Retry-After" header
            } else {
                // No recommended retry delay.. 30 seconds is a good bet
                delay = 30;
            }

            Logger.Log(LogType.SystemActivity, "Discord bot ratelimited! Trying again in {0} seconds..", delay);
            Thread.Sleep(TimeSpan.FromSeconds(delay + 0.5f));
        }
    }
}
