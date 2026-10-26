using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>
    /// The web page that shows a world's discovered map to players who are not in game (served by
    /// <see cref="WorldMapWebServer"/>): the picture with its places on top, which can be dragged and zoomed, and a
    /// legend. It reloads itself when the map changes. Self-contained: no script, font or style is fetched from
    /// anywhere else.
    /// </summary>
    public static class WorldMapWebPage
    {
        // How often an open page asks whether the map changed, and where the players are, in milliseconds.
        private const int PollMilliseconds = 120_000;
        private const int PlayersPollMilliseconds = 5_000;

        private static readonly (WorldLocationKind Kind, string Css, string Color)[] Kinds =
        {
            (WorldLocationKind.Spawn, "spawn", "#ffffff"),
            (WorldLocationKind.Boss, "boss", "#c0392b"),
            (WorldLocationKind.Trader, "trader", "#e8b93c"),
            (WorldLocationKind.Dungeon, "dungeon", "#9b7bd0"),
            (WorldLocationKind.Camp, "camp", "#e0842e"),
        };

        /// <summary>Builds the page for <paramref name="picture"/>, with the given places (those outside the picture are left out).</summary>
        public static string Build(string worldName, WorldMapPicture picture, IEnumerable<WorldMapMarker> markers,
            DateTime updatedAt, string version)
        {
            var places = new List<object>();
            foreach (var marker in markers)
            {
                var (fx, fy) = picture.ToFraction(marker.X, marker.Z);
                if (fx is < 0 or > 1 || fy is < 0 or > 1) continue;
                var css = Kinds.First(k => k.Kind == marker.Kind).Css;
                places.Add(new { n = marker.Name, k = css, x = Math.Round(fx * 100, 3), y = Math.Round(fy * 100, 3) });
            }

            var shown = Kinds.Where(k => markers.Any(m => m.Kind == k.Kind)).ToList();
            var legend = string.Concat(shown.Select(k =>
                $"<span class=\"key\"><i class=\"dot {k.Css}\"></i>{Html(KindName(k.Kind))}</span>")) +
                $"<span class=\"key\" id=\"playerkey\" hidden><i class=\"dot player\"></i>{Html(Strings.MapWeb_Players)}</span>";
            var colors = string.Concat(Kinds.Select(k => $".{k.Css}{{background:{k.Color}}}")) + ".player{background:#3ddc84}";

            var title = string.Format(CultureInfo.CurrentCulture, Strings.MapWeb_Title, worldName);
            var updated = string.Format(CultureInfo.CurrentCulture, Strings.MapWeb_Updated, updatedAt);

            // System.Text.Json escapes <, > and & by default, so the data cannot close the script element.
            var r = picture.Region;
            var data = JsonSerializer.Serialize(new
            {
                w = picture.Width, h = picture.Height, v = version, places,
                left = r.Left, top = r.Top, mpp = r.MetersPerPixel,
            });

            var page = new StringBuilder();
            page.Append("<!doctype html><html lang=\"").Append(Html(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)).Append("\"><head>")
                .Append("<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
                .Append("<title>").Append(Html(title)).Append("</title><style>")
                .Append(Style).Append(colors)
                .Append("</style></head><body><header><h1>").Append(Html(title)).Append("</h1><p>").Append(Html(updated))
                .Append("</p></header><main id=\"view\"><div id=\"world\"><img id=\"map\" alt=\"").Append(Html(title))
                .Append("\" src=\"map.png?v=").Append(Html(Uri.EscapeDataString(version))).Append("\"></div>")
                .Append("<button id=\"reset\" type=\"button\">").Append(Html(Strings.MapWeb_Reset)).Append("</button></main>")
                .Append("<footer><div class=\"legend\">").Append(legend).Append("</div><p>").Append(Html(Strings.MapWeb_Hint))
                .Append("</p></footer><script>const MAP=").Append(data).Append(";const POLL=").Append(PollMilliseconds).Append(";const PLAYERS_POLL=").Append(PlayersPollMilliseconds).Append(';')
                .Append(Script).Append("</script></body></html>");
            return page.ToString();
        }

        /// <summary>The page shown before the app has drawn the map.</summary>
        public static string NotReady()
            => "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
               "<meta http-equiv=\"refresh\" content=\"30\"><style>" + Style + "</style></head><body><header><p>" +
               Html(Strings.MapWeb_NotReady) + "</p></header></body></html>";

        private static string KindName(WorldLocationKind kind) => kind switch
        {
            WorldLocationKind.Spawn => Strings.Map_Layer_Spawn,
            WorldLocationKind.Boss => Strings.Map_Layer_Bosses,
            WorldLocationKind.Trader => Strings.Map_Layer_Traders,
            WorldLocationKind.Dungeon => Strings.Map_Layer_Dungeons,
            _ => Strings.Map_Layer_Camps,
        };

        private static string Html(string text) => WebUtility.HtmlEncode(text);

        private const string Style =
            "*{box-sizing:border-box}html,body{margin:0;height:100%}" +
            "body{display:flex;flex-direction:column;background:#1f160f;color:#f0d9a0;font:15px/1.4 system-ui,-apple-system,'Segoe UI',sans-serif}" +
            "header,footer{padding:10px 16px}header{border-bottom:2px solid #d4a63a}footer{border-top:2px solid #d4a63a;font-size:13px}" +
            "h1{margin:0;font-size:20px;color:#d4a63a}header p,footer p{margin:2px 0 0;opacity:.8}" +
            "main{position:relative;flex:1;overflow:hidden;background:#d8c49a;touch-action:none;cursor:grab}" +
            "main.drag{cursor:grabbing}#world{position:absolute;left:0;top:0;transform-origin:0 0}" +
            "#map{display:block;image-rendering:auto;user-select:none;-webkit-user-drag:none}" +
            ".place{position:absolute;transform:translate(-50%,-50%) scale(var(--inv,1));transform-origin:center;white-space:nowrap}" +
            ".dot{display:inline-block;width:12px;height:12px;border-radius:50%;border:2px solid #1f160f;vertical-align:middle}" +
            ".place .dot{display:block}" +
            ".label{position:absolute;left:16px;top:-3px;padding:1px 5px;border-radius:3px;background:rgba(31,22,15,.85);color:#f0d9a0;font-size:12px;pointer-events:none}" +
            ".legend{display:flex;flex-wrap:wrap;gap:4px 14px}.key .dot{margin-right:6px}" +
            "#reset{position:absolute;right:10px;top:10px;padding:6px 10px;border:1px solid #d4a63a;border-radius:4px;background:rgba(31,22,15,.85);color:#d4a63a;font:inherit;cursor:pointer}";

        private const string Script =
            "const view=document.getElementById('view'),world=document.getElementById('world'),img=document.getElementById('map');" +
            "img.width=MAP.w;img.height=MAP.h;world.style.width=MAP.w+'px';world.style.height=MAP.h+'px';" +
            "for(const p of MAP.places){const e=document.createElement('div');e.className='place';e.style.left=p.x+'%';e.style.top=p.y+'%';e.title=p.n;" +
            "const d=document.createElement('i');d.className='dot '+p.k;e.appendChild(d);" +
            "if(p.k==='spawn'||p.k==='boss'||p.k==='trader'){const l=document.createElement('span');l.className='label';l.textContent=p.n;e.appendChild(l);}" +
            "world.appendChild(e);}" +
            "let s=1,x=0,y=0,min=.1;" +
            "function apply(){world.style.transform='translate('+x+'px,'+y+'px) scale('+s+')';world.style.setProperty('--inv',1/s);}" +
            "function fit(){const r=view.getBoundingClientRect();s=Math.min(r.width/MAP.w,r.height/MAP.h);min=s/2;x=(r.width-MAP.w*s)/2;y=(r.height-MAP.h*s)/2;apply();}" +
            "function zoom(f,cx,cy){const n=Math.min(8,Math.max(min,s*f));x=cx-(cx-x)*n/s;y=cy-(cy-y)*n/s;s=n;apply();}" +
            "view.addEventListener('wheel',e=>{e.preventDefault();const r=view.getBoundingClientRect();zoom(Math.exp(-e.deltaY*.0015),e.clientX-r.left,e.clientY-r.top);},{passive:false});" +
            "const ptrs=new Map();let pinch=0;" +
            "view.addEventListener('pointerdown',e=>{if(e.target.id==='reset')return;view.setPointerCapture(e.pointerId);ptrs.set(e.pointerId,{x:e.clientX,y:e.clientY});view.classList.add('drag');});" +
            "view.addEventListener('pointermove',e=>{const p=ptrs.get(e.pointerId);if(!p)return;" +
            "if(ptrs.size===1){x+=e.clientX-p.x;y+=e.clientY-p.y;apply();}" +
            "p.x=e.clientX;p.y=e.clientY;" +
            "if(ptrs.size===2){const [a,b]=[...ptrs.values()];const d=Math.hypot(a.x-b.x,a.y-b.y);const r=view.getBoundingClientRect();" +
            "if(pinch)zoom(d/pinch,(a.x+b.x)/2-r.left,(a.y+b.y)/2-r.top);pinch=d;}});" +
            "function up(e){ptrs.delete(e.pointerId);pinch=0;if(!ptrs.size)view.classList.remove('drag');}" +
            "view.addEventListener('pointerup',up);view.addEventListener('pointercancel',up);" +
            "document.getElementById('reset').addEventListener('click',fit);addEventListener('resize',fit);fit();" +
            "setInterval(()=>fetch('version',{cache:'no-store'}).then(r=>r.text()).then(t=>{if(t&&t!==MAP.v)location.reload();}).catch(()=>{}),POLL);" +
            "const players=[];function showPlayers(list){for(const e of players)e.remove();players.length=0;" +
            "for(const p of list){const fx=(p.x-MAP.left)/(MAP.w*MAP.mpp),fy=(MAP.top-p.z)/(MAP.h*MAP.mpp);if(fx<0||fx>1||fy<0||fy>1)continue;" +
            "const e=document.createElement('div');e.className='place';e.style.left=fx*100+'%';e.style.top=fy*100+'%';e.title=p.n;" +
            "const d=document.createElement('i');d.className='dot player';e.appendChild(d);" +
            "const l=document.createElement('span');l.className='label';l.textContent=p.n;e.appendChild(l);world.appendChild(e);players.push(e);}" +
            "document.getElementById('playerkey').hidden=!players.length;}" +
            "function pollPlayers(){fetch('players',{cache:'no-store'}).then(r=>r.json()).then(showPlayers).catch(()=>{});}" +
            "pollPlayers();setInterval(pollPlayers,PLAYERS_POLL);";
    }
}
