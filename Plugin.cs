using AIMP.SDK;

using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json;
using System.Diagnostics;
using System.Linq;
using AIMP.SDK.Playlist;
using AIMP.SDK.Threading;
using AIMP.SDK.Player;
using AIMP.SDK.MessageDispatcher;
using System.Runtime.InteropServices;
using System.IO;
using System.Web;

namespace http_control
{
    [AimpPlugin("http_control", "a", "1.0.0.0", AimpPluginType = AimpPluginType.Addons, Description = "HTTP control service")]
    public class Plugin : AimpPlugin
    {
        HttpListener listener;
        public override void Dispose()
        {
            listener.Close();
            if (File.Exists("C:/log.txt"))
                File.Delete("C:/log.txt");
        }

        public override void Initialize()
        {
            if (Player.Win32Manager.GetAimpHandle() == IntPtr.Zero)
                throw new Exception();

            listener = new HttpListener();
            listener.Prefixes.Add("http://*:5555/");
            listener.Start();

            listener.GetContextAsync().ContinueWith(HandleRequest);
        }

        private async Task HandleRequest(Task<HttpListenerContext> task)
        {
            object resObj = null;

            var req = task.Result.Request;
            var res = task.Result.Response;
            try
            {
                res.ContentType = "application/json;charset=utf-8;";

                var raw = new StringBuilder(req.RawUrl);
                if (req.RawUrl.IndexOf('?') != -1)
                    raw.Remove(req.RawUrl.IndexOf('?'), raw.Length - req.RawUrl.IndexOf('?'));
                
                switch (raw.ToString().Substring(1).ToLower())
                {
                    case "play":
                        if (req.QueryString["id"] != null)
                        {
                            int id;
                            if (!int.TryParse(req.QueryString["id"], out id) || id <= 0)
                            {
                                res.StatusCode = 400;
                                break;
                            }

                            Player.ServiceThreads.ExecuteInMainThread(new GetPlayerData(() =>
                            {
                                if (id > Player.CurrentPlaylistItem.PlayList.GetItemCount())
                                {
                                    res.StatusCode = 400;
                                    return;
                                }
                                Player.Play(Player.CurrentPlaylistItem.PlayList.GetItem(id - 1).Result);
                            }, async () =>
                            {
                                await TailAction(resObj, res);
                                await listener.GetContextAsync().ContinueWith(HandleRequest);
                            }), true);
                            break;
                        }
                        else if (req.QueryString["text"] != null)
                        {
                            Player.ServiceThreads.ExecuteInMainThread(new GetPlayerData(() =>
                            {
                                var s = GetSongs().Where(t => t.Song.ToLower().Contains(HttpUtility.UrlDecode(req.Url.Query
                                    .Substring(req.Url.Query.IndexOf('=') + 1)).ToLower()));
                                if (s.Count() == 0)
                                    res.StatusCode = 204;
                                else if (s.Count() == 1)
                                    Player.Play(Player.CurrentPlaylistItem.PlayList.GetItem(s.First().Id).Result);
                                else
                                    resObj = s.ToArray();
                            }, async () =>
                            {
                                await TailAction(resObj, res);
                                await listener.GetContextAsync().ContinueWith(HandleRequest);
                            }), true);
                            break;
                        }
                        Player.Pause();
                        break;
                    case "pause":
                        Player.Pause();
                        break;
                    case "next":
                        Player.GoToNext();
                        break;
                    case "prev":
                        Player.GoToPrev();
                        break;
                    case "volume":
                        if (req.QueryString["value"] != null)
                        {
                            int vol;
                            if (!int.TryParse(req.QueryString["value"], out vol) || vol < 0 || vol > 100)
                            {
                                res.StatusCode = 400;
                                break;
                            }
                            Player.Volume = (float)vol / 100;
                        }
                        else
                            res.StatusCode = 400;
                        break;
                    case "repeat":
                        if ((long)SendMessage(FindWindowA("AIMP2_RemoteInfo", "AIMP2_RemoteInfo"), 0x0400 + 0x77, (IntPtr)0x70, IntPtr.Zero) == 0)
                            SendMessage(FindWindowA("AIMP2_RemoteInfo", "AIMP2_RemoteInfo"), 0x0400 + 0x77, (IntPtr)0x71, (IntPtr)1);
                        else
                            SendMessage(FindWindowA("AIMP2_RemoteInfo", "AIMP2_RemoteInfo"), 0x0400 + 0x77, (IntPtr)0x71, (IntPtr)0);
                        break;
                    case "shuffle":
                        if ((long)SendMessage(FindWindowA("AIMP2_RemoteInfo", "AIMP2_RemoteInfo"), 0x0400 + 0x77, (IntPtr)0x80, IntPtr.Zero) == 0)
                            SendMessage(FindWindowA("AIMP2_RemoteInfo", "AIMP2_RemoteInfo"), 0x0400 + 0x77, (IntPtr)0x81, (IntPtr)1);
                        else
                            SendMessage(FindWindowA("AIMP2_RemoteInfo", "AIMP2_RemoteInfo"), 0x0400 + 0x77, (IntPtr)0x81, (IntPtr)0);
                        break;
                    case "mute":
                        Player.IsMute = !Player.IsMute;
                        break;
                    case "info":
                        Player.ServiceThreads.ExecuteInMainThread(new GetPlayerData(() =>
                        {
                            resObj = new
                            {
                                title = Player.CurrentFileInfo.Title,
                                artist = Player.CurrentFileInfo.Artist,
                                index = Player.CurrentPlaylistItem.Index,
                                playing = Player.State == AimpPlayerState.Playing,
                                volume = Player.Volume * 100,
                                repeat = (long)SendMessage(FindWindowA("AIMP2_RemoteInfo", "AIMP2_RemoteInfo"), 0x0400 + 0x77, (IntPtr)0x70, IntPtr.Zero) != 0,
                                shuffle = (long)SendMessage(FindWindowA("AIMP2_RemoteInfo", "AIMP2_RemoteInfo"), 0x0400 + 0x77, (IntPtr)0x80, IntPtr.Zero) != 0,
                            };
                        }, async () =>
                        {
                            await TailAction(resObj, res);
                            await listener.GetContextAsync().ContinueWith(HandleRequest);
                        }), true);
                        break;
                    case "playlist":
                        Player.ServiceThreads.ExecuteInMainThread(new GetPlayerData(() =>
                        {
                            resObj = GetSongs().ToArray();
                        }, async () =>
                        {
                            await TailAction(resObj, res);
                            await listener.GetContextAsync().ContinueWith(this.HandleRequest);
                        }), true);
                        break;
                    case "queue":
                        if (req.QueryString["id"] != null)
                        {
                            int id;
                            if (!int.TryParse(req.QueryString["id"], out id) || id <= 0)
                            {
                                res.StatusCode = 400;
                                break;
                            }

                            Player.ServiceThreads.ExecuteInMainThread(new GetPlayerData(() =>
                            {
                                if (id > Player.CurrentPlaylistItem.PlayList.GetItemCount())
                                {
                                    res.StatusCode = 400;
                                    return;
                                }
                                Player.ServicePlaylistManager.PlaylistQueue.Add(Player.CurrentPlaylistItem.PlayList.GetItem(id - 1).Result, false);
                            }, async () =>
                            {
                                await TailAction(resObj, res);
                                await listener.GetContextAsync().ContinueWith(HandleRequest);
                            }), true);
                            break;
                        }
                        else if (req.QueryString["text"] != null)
                        {
                            Player.ServiceThreads.ExecuteInMainThread(new GetPlayerData(() =>
                            {
                                var s = GetSongs().Where(t => t.Song.ToLower().Contains(HttpUtility.UrlDecode(req.Url.Query
                                    .Substring(req.Url.Query.IndexOf('=') + 1)).ToLower()));
                                if (s.Count() == 0)
                                    res.StatusCode = 204;
                                else if (s.Count() == 1)
                                    Player.ServicePlaylistManager.PlaylistQueue.Add(Player.CurrentPlaylistItem.PlayList.GetItem(s.First().Id).Result, false);
                                else
                                    resObj = s.ToArray();
                            }, async () =>
                            {
                                await TailAction(resObj, res);
                                await listener.GetContextAsync().ContinueWith(HandleRequest);
                            }), true);
                            break;
                        }
                        res.StatusCode = 400;
                        break;
                    default:
                        res.ContentType = "";
                        res.StatusCode = 400;
                        break;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText("C:/log.txt", $"{ex}{ex.Message}\n{ex.StackTrace}\n");
                res.StatusCode = 500;
            }

            await TailAction(resObj, res);
            await listener.GetContextAsync().ContinueWith(HandleRequest);
        }

        private System.Collections.Generic.IEnumerable<ISong> GetSongs()
        {
            return Player.ServicePlaylistManager.GetActivePlaylist().Result
                                        .GetFiles(PlaylistGetFilesFlag.All).Result.Select((t, i) =>
                                        new ISong { Id = i, Song = Path.GetFileNameWithoutExtension(t) });
        }

        private async Task TailAction(object resObj, HttpListenerResponse res)
        {
            if (resObj != null)
            {
                var str = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(resObj));
                await res.OutputStream.WriteAsync(str, 0, str.Length);
            }
            res.Close();
        }

        [DllImport("user32", SetLastError = true)]
        private static extern IntPtr FindWindowA(string lpClassName, string lpWindowName);


        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

        class ISong
        {
            public int Id { get; set; }
            public string Song { get; set; }
        }
    }

    class GetPlayerData : IAimpTask
    {
        private readonly Action action;
        private readonly Action tail;

        public GetPlayerData(Action action, Action tail)
        {
            this.action = action;
            this.tail = tail;
        }
        public AimpActionResult Execute(IAimpTaskOwner owner)
        {
            action();
            tail();
            return new AimpActionResult(ActionResultType.OK);
        }
    }
}
