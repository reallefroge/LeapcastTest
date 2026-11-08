namespace LeapcastStudioAI;

/// <summary>The overlay pages (transparent; a Browser source shows them over the game).</summary>
public static class OverlayPages
{
    public const string Index = """
<!doctype html><html><head><meta charset="utf-8"><title>Leapcast overlays</title>
<style>body{font:15px Segoe UI,sans-serif;background:#0d1017;color:#e8ecf3;padding:24px}a{color:#7cc4ff}</style></head>
<body><h2>Leapcast overlays</h2><p>Add these as Browser sources:</p>
<ul><li><a href="/alerts">/alerts</a> — alerts (800 × 300)</li><li><a href="/chat">/chat</a> — chat (400 × 600). <code>?fade=60</code> hides messages after 60 s.</li></ul></body></html>
""";

    public const string Alerts = """
<!doctype html><html><head><meta charset="utf-8"><title>Leapcast alerts</title>
<style>
html,body{margin:0;background:transparent;overflow:hidden;width:100%;height:100%;font-family:"Segoe UI Black","Segoe UI",Arial,sans-serif}
#stage{position:absolute;inset:0;display:flex;align-items:center;justify-content:center}
.card{position:relative;display:flex;align-items:center;gap:22px;padding:22px 34px 22px 26px;border-radius:22px;
  background:linear-gradient(135deg,rgba(14,18,30,.92),rgba(24,30,48,.92));box-shadow:0 0 0 3px var(--c),0 0 38px var(--c),0 18px 40px rgba(0,0,0,.55);
  color:#fff;max-width:92%;animation:in .7s cubic-bezier(.2,1.6,.4,1) both}
.card.out{animation:out .5s ease-in both}
.icon{font-size:66px;line-height:1;filter:drop-shadow(0 4px 10px rgba(0,0,0,.5));animation:bob 1.2s ease-in-out infinite alternate}
.head{font-size:36px;font-weight:900;letter-spacing:.3px;text-shadow:0 3px 0 rgba(0,0,0,.45);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.head b{color:var(--c);filter:brightness(1.35)}
.sub{margin-top:6px;font:600 22px "Segoe UI",Arial,sans-serif;color:#d8deea;max-width:560px;overflow:hidden;text-overflow:ellipsis;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical}
.bar{position:absolute;left:22px;right:22px;bottom:8px;height:4px;border-radius:2px;background:rgba(255,255,255,.15);overflow:hidden}
.bar i{display:block;height:100%;background:var(--c);transform-origin:left;animation:shrink linear both}
.spark{position:absolute;width:10px;height:10px;border-radius:2px;top:50%;left:50%;opacity:0;animation:pop 1.1s ease-out forwards}
@keyframes in{0%{transform:translateY(-40px) scale(.6);opacity:0}100%{transform:none;opacity:1}}
@keyframes out{to{transform:translateY(-30px) scale(.9);opacity:0}}
@keyframes bob{from{transform:translateY(-3px) rotate(-6deg)}to{transform:translateY(3px) rotate(6deg)}}
@keyframes shrink{from{transform:scaleX(1)}to{transform:scaleX(0)}}
@keyframes pop{0%{opacity:1;transform:translate(-50%,-50%) scale(1)}100%{opacity:0;transform:translate(var(--x),var(--y)) rotate(var(--r)) scale(.6)}}
</style></head><body><div id="stage"></div>
<script>
const colors={twitch:"#9146FF",youtube:"#FF2D55",donation:"#2ECC71"};
const icons={Follow:"💜",Subscriber:"🔔",Sub:"⭐",Resub:"🌟",GiftSub:"🎁",Bits:"💎",Raid:"🚀",SuperChat:"💰",SuperSticker:"💰",Member:"⭐",MemberMilestone:"🏆",GiftMembers:"🎁",Donation:"💸"};
const stage=document.getElementById("stage");
function esc(s){return (s||"").replace(/[&<>"]/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]))}
function show(a){
  stage.innerHTML="";
  const c=colors[a.p]||"#3DD6F5";
  const card=document.createElement("div");card.className="card";card.style.setProperty("--c",c);
  const head=esc(a.head).replace(esc(a.u),"<b>"+esc(a.u)+"</b>");
  card.innerHTML=`<div class="icon">${icons[a.kind]||"✨"}</div><div><div class="head">${head}</div>${a.sub?`<div class="sub">${esc(a.sub)}</div>`:""}</div><div class="bar"><i style="animation-duration:${a.ms}ms"></i></div>`;
  stage.appendChild(card);
  const big=["GiftSub","GiftMembers","Raid","SuperChat","Donation","Bits"].includes(a.kind);
  for(let i=0;i<(big?46:22);i++){const s=document.createElement("div");s.className="spark";
    const ang=Math.random()*Math.PI*2,d=160+Math.random()*260;
    s.style.cssText=`--x:${Math.cos(ang)*d}px;--y:${Math.sin(ang)*d*0.6}px;--r:${Math.random()*720}deg;background:${i%3?c:"#FFD93D"};animation-delay:${Math.random()*0.25}s`;
    stage.appendChild(s);}
  setTimeout(()=>card.classList.add("out"),a.ms-500);
  setTimeout(()=>{if(stage.contains(card))stage.innerHTML=""},a.ms+100);
}
const es=new EventSource("/events");
es.onmessage=e=>{const m=JSON.parse(e.data);if(m.t==="alert")show(m)};
</script></body></html>
""";

    public const string Chat = """
<!doctype html><html><head><meta charset="utf-8"><title>Leapcast chat</title>
<style>
html,body{margin:0;background:transparent;overflow:hidden;height:100%;font:700 19px "Segoe UI",Arial,sans-serif;color:#fff}
#list{position:absolute;left:0;right:0;bottom:0;padding:8px 10px;display:flex;flex-direction:column;gap:6px}
.m{line-height:1.35;text-shadow:0 0 3px #000,0 2px 2px #000,0 0 6px rgba(0,0,0,.9);word-wrap:break-word;animation:in .35s ease-out both;transition:opacity .8s}
.m.alert{background:rgba(255,217,61,.18);border-left:4px solid #FFD93D;padding:4px 8px;border-radius:6px}
.p{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:6px;vertical-align:middle;box-shadow:0 0 4px #000}
.p.twitch{background:#9146FF}.p.youtube{background:#FF2D55}
.b{display:inline-block;font:800 11px "Segoe UI",Arial,sans-serif;padding:1px 5px;border-radius:4px;margin-right:4px;vertical-align:2px;text-shadow:none}
.b.broadcaster{background:#E91916}.b.moderator{background:#00AD03}.b.vip{background:#E005B9}.b.subscriber,.b.member{background:#6441A5}.b.verified{background:#555}
.n{font-weight:900}
img.e{height:1.5em;vertical-align:middle;margin:-.2em .05em}
@keyframes in{from{opacity:0;transform:translateX(-12px)}to{opacity:1;transform:none}}
</style></head><body><div id="list"></div>
<script>
const list=document.getElementById("list");
const fade=parseInt(new URLSearchParams(location.search).get("fade")||"0");
const names={broadcaster:"HOST",moderator:"MOD",vip:"VIP",subscriber:"SUB",member:"MEMBER",verified:"✓"};
function esc(s){return (s||"").replace(/[&<>"]/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]))}
const ids=new Set();
const es=new EventSource("/events");
es.onmessage=e=>{
  const m=JSON.parse(e.data); if(m.t!=="chat"||ids.has(m.id))return; ids.add(m.id);
  const d=document.createElement("div");d.className="m"+(m.alert?" alert":"");
  const badges=(m.b||[]).map(b=>`<span class="b ${b}">${names[b]||b}</span>`).join("");
  const body=(m.parts||[]).map(x=>x.img?`<img class="e" src="${esc(x.img)}" alt="${esc(x.t)}">`:esc(x.t)).join("");
  d.innerHTML=`<span class="p ${m.p}"></span>${badges}<span class="n" style="color:${esc(m.c)}">${esc(m.u)}</span>: ${body}`;
  list.appendChild(d);
  while(list.children.length>60)list.removeChild(list.firstChild);
  if(fade>0)setTimeout(()=>{d.style.opacity=0;setTimeout(()=>d.remove(),900)},fade*1000);
};
</script></body></html>
""";
}
