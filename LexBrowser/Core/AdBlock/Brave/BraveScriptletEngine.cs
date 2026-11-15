using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LexBrowser.Core.AdBlock.Brave;

/// <summary>
/// Compone scripts por documento a partir de los scriptlets (<c>##+js(...)</c>),
/// selectores específicos y reglas procedurales de Brave, más el script bootstrap compartido.
/// </summary>
public static class BraveScriptletEngine
{
    private const int MaxStyleRuleSize = 64 * 1024;

    /// <summary>
    /// Analiza los argumentos de <c>##+js(...)</c> replicando la semántica de uBO / adblock-rust:
    /// respeta comillas externas (<c>'</c>, <c>"</c>, <c>`</c>) al inicio del argumento y escapado <c>\,</c> en argumentos sin comillas.
    /// </summary>
    public static List<string> ParseScriptletArgs(string? rawInner)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(rawInner))
            return result;

        string s = rawInner.Trim();
        int i = 0;
        int len = s.Length;

        while (i <= len)
        {
            while (i < len && char.IsWhiteSpace(s[i]))
                i++;

            if (i >= len)
            {
                // Si terminaba exactamente con coma previa, agrega argumento vacío si hubo coma; sino termina.
                break;
            }

            char first = s[i];
            if (first == '\'' || first == '"' || first == '`')
            {
                char quote = first;
                i++;
                var sb = new StringBuilder();
                while (i < len)
                {
                    char c = s[i];
                    if (c == '\\' && i + 1 < len)
                    {
                        if (s[i + 1] == quote)
                        {
                            sb.Append(quote);
                            i += 2;
                            continue;
                        }
                        if (s[i + 1] == '\\' && i + 2 < len && s[i + 2] == quote)
                        {
                            sb.Append("\\\\");
                            i += 2;
                            continue;
                        }
                    }
                    if (c == quote)
                    {
                        i++;
                        break;
                    }
                    sb.Append(c);
                    i++;
                }

                // Saltar espacios tras comilla de cierre hasta la coma o fin
                while (i < len && s[i] != ',')
                    i++;

                result.Add(sb.ToString());
                if (i < len && s[i] == ',')
                {
                    i++;
                    if (i == len)
                        result.Add(string.Empty);
                }
            }
            else
            {
                var sb = new StringBuilder();
                while (i < len)
                {
                    char c = s[i];
                    if (c == '\\' && i + 1 < len && s[i + 1] == ',')
                    {
                        sb.Append(',');
                        i += 2;
                        continue;
                    }
                    if (c == ',')
                        break;
                    sb.Append(c);
                    i++;
                }

                result.Add(sb.ToString().Trim());
                if (i < len && s[i] == ',')
                {
                    i++;
                    if (i == len)
                        result.Add(string.Empty);
                }
            }
        }

        return result;
    }

    public static bool TryBuildScriptletCall(
        string rawInner,
        BraveResourceStore resources,
        out string scriptletJs,
        bool allowPrivileged = false)
    {
        scriptletJs = string.Empty;
        if (string.IsNullOrWhiteSpace(rawInner) || resources == null)
            return false;

        List<string> args = ParseScriptletArgs(rawInner);
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return false;

        if (!TryResolveJsResource(resources, args[0], allowPrivileged, out BraveResourceEntry? entry) || entry == null)
            return false;

        var orderedDeps = new List<BraveResourceEntry>();
        var visitedDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!CollectDependencies(entry, resources, allowPrivileged, orderedDeps, visitedDeps))
            return false;

        string mainBody = entry.GetUtf8Text();
        if (string.IsNullOrWhiteSpace(mainBody))
            return false;

        var inner = new StringBuilder(mainBody.Length + 512);
        for (int i = 0; i < orderedDeps.Count; i++)
        {
            string depCode = orderedDeps[i].GetUtf8Text();
            if (!string.IsNullOrWhiteSpace(depCode))
            {
                inner.AppendLine(depCode);
            }
        }

        bool isTemplate = entry.IsTemplate || mainBody.Contains("{{1}}", StringComparison.Ordinal);
        if (isTemplate)
        {
            string rendered = mainBody;
            for (int k = 1; k <= 9; k++)
            {
                string val = k < args.Count ? EscapeJsRawStringBody(args[k]) : string.Empty;
                rendered = rendered.Replace($"{{{{{k}}}}}", val, StringComparison.Ordinal);
            }
            inner.AppendLine(rendered);
        }
        else if (TryExtractLeadingFunctionName(mainBody, out string fnName))
        {
            inner.AppendLine(mainBody);
            inner.Append(fnName);
            inner.Append('(');
            for (int a = 1; a < args.Count; a++)
            {
                if (a > 1)
                    inner.Append(", ");
                AppendJsStringLiteral(inner, args[a]);
            }
            inner.AppendLine(");");
        }
        else
        {
            inner.AppendLine(mainBody);
        }

        scriptletJs = $"try{{(function(){{'use strict';var scriptletGlobals={{}};\n{inner}\n}})();}}catch(_e){{try{{var _d=window.__lexBraveDiag;if(_d&&_d.scriptletErrs)_d.scriptletErrs.push(String(_e));}}catch(_){{}}}}\n";
        return true;
    }

    public static string? BuildHostInjectionScript(
        string? host,
        HostCosmeticResources cosmetic,
        BraveResourceStore resources,
        EffectiveSitePolicy policy,
        bool allowPrivileged = false)
    {
        if (!policy.ShieldEnabled || !policy.Cosmetic || string.IsNullOrWhiteSpace(host) || cosmetic == null)
            return null;

        string normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || ShieldIsolation.IsIsolatedHost(normalized))
            return null;

        var scriptletBlocks = new List<string>();
        for (int i = 0; i < cosmetic.Scriptlets.Count; i++)
        {
            if (TryBuildScriptletCall(cosmetic.Scriptlets[i], resources, out string callJs, allowPrivileged))
                scriptletBlocks.Add(callJs);
        }

        string hostCss = BuildHostCss(cosmetic);
        bool hasProc = cosmetic.ProceduralRules.Count > 0;

        if (scriptletBlocks.Count == 0 && string.IsNullOrEmpty(hostCss) && !hasProc && !cosmetic.GenericHide)
            return null;

        var sb = new StringBuilder(4096);
        sb.AppendLine("(function(){");
        sb.AppendLine("'use strict';");
        sb.Append("var __h=(location.hostname||'').toLowerCase().replace(/\\.+$/,'');");
        sb.Append("if(__h!==");
        AppendJsStringLiteral(sb, normalized);
        sb.AppendLine(")return;");

        if (cosmetic.GenericHide)
            sb.AppendLine("window.__lexBraveGenericHide=true;");

        for (int i = 0; i < scriptletBlocks.Count; i++)
            sb.Append(scriptletBlocks[i]);

        if (!string.IsNullOrEmpty(hostCss))
        {
            sb.Append("var __css=");
            AppendJsStringLiteral(sb, hostCss);
            sb.AppendLine(";");
            sb.AppendLine("(function __applyHostCss(){");
            sb.AppendLine("  var fn=function(){");
            sb.AppendLine("    var root=document.head||document.documentElement;if(!root)return false;");
            sb.AppendLine("    var st=document.getElementById('lex-brave-host');");
            sb.AppendLine("    if(!st){st=document.createElement('style');st.id='lex-brave-host';root.appendChild(st);}");
            sb.AppendLine("    if(st.textContent!==__css)st.textContent=__css;");
            sb.AppendLine("    return true;");
            sb.AppendLine("  };");
            sb.AppendLine("  if(!fn()){try{var mo=new MutationObserver(function(){if(fn())mo.disconnect();});mo.observe(document,{childList:true,subtree:true});}catch(_){}}");
            sb.AppendLine("})();");
        }

        if (hasProc)
        {
            sb.Append("var __proc=[");
            for (int i = 0; i < cosmetic.ProceduralRules.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendJsStringLiteral(sb, cosmetic.ProceduralRules[i]);
            }
            sb.AppendLine("];");
            sb.AppendLine("window.__lexBravePendingProcedural=(window.__lexBravePendingProcedural||[]).concat(__proc);");
            sb.AppendLine("if(typeof window.__lexBraveApplyProcedural==='function'){try{window.__lexBraveApplyProcedural();}catch(_){}}");
        }

        sb.AppendLine("})();");
        return sb.ToString();
    }

    public static string BuildMiscGenericCss(BraveCosmeticEngine engine, HostCosmeticResources? hostCosmetic)
    {
        if (engine == null || engine.MiscGenericHide.Count == 0)
            return string.Empty;

        var unhidden = hostCosmetic?.UnhiddenSelectors;
        if (unhidden != null && unhidden.Contains("*"))
            return string.Empty;

        var kept = new List<string>(engine.MiscGenericHide.Count);
        for (int i = 0; i < engine.MiscGenericHide.Count; i++)
        {
            string sel = engine.MiscGenericHide[i];
            if (unhidden != null && unhidden.Contains(sel))
                continue;
            kept.Add(sel);
        }

        if (kept.Count == 0)
            return string.Empty;

        return BraveCosmeticEngine.FormatHideCssChunks(kept);
    }

    internal static string BuildScriptletsBundle(
        HostCosmeticResources cosmetic,
        BraveResourceStore resources,
        bool allowPrivileged = false)
    {
        if (cosmetic == null || resources == null || cosmetic.Scriptlets.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(1024);
        for (int i = 0; i < cosmetic.Scriptlets.Count; i++)
        {
            if (TryBuildScriptletCall(cosmetic.Scriptlets[i], resources, out string callJs, allowPrivileged))
                sb.Append(callJs);
        }
        return sb.ToString();
    }

    internal static string BuildHostHideCss(HostCosmeticResources cosmetic)
        => cosmetic?.HideSelectors.Count > 0
            ? BraveCosmeticEngine.FormatHideCssChunks(cosmetic.HideSelectors, maxBatchCount: 20, maxBatchChars: MaxStyleRuleSize)
            : string.Empty;

    internal static string BuildHostStyleCss(HostCosmeticResources cosmetic)
    {
        if (cosmetic == null || cosmetic.StyleCssRules.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (int i = 0; i < cosmetic.StyleCssRules.Count; i++)
        {
            sb.Append(cosmetic.StyleCssRules[i]);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildHostCss(HostCosmeticResources cosmetic)
    {
        string hide = BuildHostHideCss(cosmetic);
        string style = BuildHostStyleCss(cosmetic);
        if (string.IsNullOrEmpty(hide))
            return style;
        if (string.IsNullOrEmpty(style))
            return hide;
        return hide + "\n" + style;
    }

    private static bool TryResolveJsResource(
        BraveResourceStore resources,
        string nameOrAlias,
        bool allowPrivileged,
        out BraveResourceEntry? entry)
    {
        string trimmed = nameOrAlias.Trim();
        if (resources.TryGetResource(trimmed, out entry, allowPrivileged) &&
            entry != null &&
            entry.Mime.Contains("javascript", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!trimmed.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
            resources.TryGetResource(trimmed + ".js", out entry, allowPrivileged) &&
            entry != null &&
            entry.Mime.Contains("javascript", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        entry = null;
        return false;
    }

    private static bool CollectDependencies(
        BraveResourceEntry entry,
        BraveResourceStore resources,
        bool allowPrivileged,
        List<BraveResourceEntry> ordered,
        HashSet<string> visited)
    {
        if (entry.Dependencies == null || entry.Dependencies.Count == 0)
            return true;

        for (int i = 0; i < entry.Dependencies.Count; i++)
        {
            string depName = entry.Dependencies[i];
            if (!visited.Add(depName))
                continue;

            if (!resources.TryGetResource(depName, out BraveResourceEntry? depEntry, allowPrivileged) || depEntry == null)
                return false;

            if (!CollectDependencies(depEntry, resources, allowPrivileged, ordered, visited))
                return false;

            ordered.Add(depEntry);
        }

        return true;
    }

    internal static bool TryExtractLeadingFunctionName(string js, out string fnName)
    {
        fnName = string.Empty;
        if (string.IsNullOrEmpty(js))
            return false;

        int i = 0;
        int len = js.Length;
        while (i < len)
        {
            while (i < len && char.IsWhiteSpace(js[i]))
                i++;
            if (i >= len)
                return false;

            if (i + 1 < len && js[i] == '/' && js[i + 1] == '/')
            {
                int nl = js.IndexOf('\n', i + 2);
                if (nl < 0) return false;
                i = nl + 1;
                continue;
            }

            if (i + 1 < len && js[i] == '/' && js[i + 1] == '*')
            {
                int end = js.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 2;
                continue;
            }

            break;
        }

        var m = Regex.Match(js.Substring(i), @"^function\s+([A-Za-z0-9_$]+)\s*\(");
        if (!m.Success)
            return false;

        fnName = m.Groups[1].Value;
        return true;
    }

    private static string EscapeJsRawStringBody(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\'': sb.Append("\\'"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '<': sb.Append("\\u003C"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    internal static void AppendJsStringLiteral(StringBuilder sb, string value)
    {
        sb.Append('"');
        sb.Append(EscapeJsRawStringBody(value));
        sb.Append('"');
    }

    /// <summary>
    /// Script ligero compartido en pestañas no aisladas cuando el motor Brave está activo:
    /// obtiene el plan del documento síncronamente vía HostObject COM (<c>lexShield</c>),
    /// ejecuta scriptlets antes del &lt;head&gt;, aplica CSP/estilos en el primer nodo del DOM,
    /// consulta selectores genéricos bajo demanda y ejecuta filtros procedurales.
    /// </summary>
    public static string BootstrapScript { get; } = BuildBootstrapScript(string.Empty);

    public static string BuildBootstrapScript(string bridgeToken)
    {
        string escapedToken = EscapeJsRawStringBody(bridgeToken ?? string.Empty);
        return BootstrapScriptTemplate.Replace("__LEX_BRIDGE_TOKEN__", escapedToken, StringComparison.Ordinal);
    }

    private const string BootstrapScriptTemplate = """
(function(){
'use strict';
if(window.__lexBraveBootstrapped)return;
window.__lexBraveBootstrapped=true;
var __lexTok="__LEX_BRIDGE_TOKEN__";
var __isTop=(window.top===window);
var __host=(location.hostname||'').toLowerCase().replace(/\.+$/,'');
var __parentHref='';
if(!__isTop){
  try{__parentHref=window.parent.location.href||'';}catch(_){}
  if(!__parentHref)try{__parentHref=document.referrer||'';}catch(_){}
}
var __effectiveHost=__host;
if(!__effectiveHost&&__parentHref){
  var __pm=/^https?:\/\/([^\/:?#]+)/i.exec(__parentHref);
  if(__pm&&__pm[1])__effectiveHost=__pm[1].toLowerCase().replace(/\.+$/,'');
}
if(!__effectiveHost)return;
if(/(^|\.)(youtube\.com|youtu\.be|youtube-nocookie\.com|youtubekids\.com|googlevideo\.com|ytimg\.com|dailymotion\.com|dmcdn\.net|dmxleo\.com)$/.test(__effectiveHost))return;

var __seenCls=new Set(),__seenIds=new Set();
var __pendCls=[],__pendIds=[];
var __initialSent=false,__scanTimer=0,__procTimer=0;
var __countedEls=new WeakSet();
var __styleTexts={};
var __pendingStyles={};
var __pendingCsp=[];
var __cspInserted=false;
var __genericAllowed=true;
var __enableOverlayDefuser=false;
var __diag=window.__lexBraveDiag={host:__effectiveHost,isTop:__isTop,hasBridge:false,hostObjType:'undefined',planOk:false,enabled:null,cosmetic:null,ghide:false,scriptletLen:0,scriptletsOk:false,scriptletErrs:[],hostCss:false,styleCss:false,mgCss:false,cspCount:0,noPopup:false,ovl:false,errs:[]};

function __applyStyleEl(id,cssText,root){
  var el=document.getElementById(id);
  if(!el){
    el=document.createElement('style');
    el.id=id;
    el.type='text/css';
    root.appendChild(el);
  }
  if(el.textContent!==cssText)el.textContent=cssText;
  if(el.isConnected){
    __styleTexts[id]=cssText;
    delete __pendingStyles[id];
  }
}
function __ensureStyle(id,cssText){
  if(!cssText)return;
  var el=document.getElementById(id);
  if(el&&el.isConnected&&__styleTexts[id]===cssText&&el.textContent===cssText)return;
  __pendingStyles[id]=cssText;
  var root=document.head||document.documentElement;
  if(!root)return;
  __applyStyleEl(id,cssText,root);
}
function __appendStyle(id,cssText){
  if(!cssText)return;
  var cur=__pendingStyles[id]||__styleTexts[id]||'';
  if(cur.indexOf(cssText)!==-1)return;
  __ensureStyle(id,cur?cur+'\n'+cssText:cssText);
}
function __removeStyle(id){
  delete __pendingStyles[id];
  delete __styleTexts[id];
  var el=document.getElementById(id);
  if(el&&el.parentNode)el.parentNode.removeChild(el);
}
function __flushCsp(){
  if(__cspInserted||!__pendingCsp.length)return;
  var h=document.head;
  if(!h)return;
  __cspInserted=true;
  var list=__pendingCsp;
  __pendingCsp=[];
  for(var i=list.length-1;i>=0;i--){
    var c=list[i];
    if(!c)continue;
    try{
      var m=document.createElement('meta');
      m.httpEquiv='Content-Security-Policy';
      m.content=c;
      h.insertBefore(m,h.firstChild);
      __diag.cspCount++;
    }catch(e){__diag.errs.push('csp:'+String(e));}
  }
}
function __reportShield(category,count,reason){
  if(!__isTop||count<=0)return;
  try{
    if(window.chrome&&window.chrome.webview&&typeof window.chrome.webview.postMessage==='function'){
      window.chrome.webview.postMessage({type:'ad_blocked',category:category||'cosmetic',reason:reason||'brave_cosmetic',url:location.href});
    }
  }catch(_){}
}
function __reportCosmetic(count,reason){
  __reportShield('cosmetic',count,reason);
}
function __installNoPopup(){
  try{
    window.open=function(){__reportShield('popup',1,'brave_csp_sandbox');return null;};
  }catch(_){}
  try{
    document.addEventListener('click',function(ev){
      var t=ev&&ev.target;
      var a=t&&t.closest?t.closest('a[target]'):null;
      if(!a)return;
      var tgt=(a.getAttribute('target')||'').toLowerCase();
      if(tgt==='_blank'||tgt==='new'){
        ev.preventDefault();
        ev.stopPropagation();
        __reportShield('popup',1,'brave_csp_sandbox_blank');
      }
    },true);
  }catch(_){}
}

var __bridge=null;
try{
  var __ho=window.chrome&&window.chrome.webview&&window.chrome.webview.hostObjects&&window.chrome.webview.hostObjects.sync;
  __diag.hostObjType=typeof (__ho&&__ho.lexShield);
  __bridge=(__ho&&__ho.lexShield)||null;
  __diag.hasBridge=!!__bridge;
}catch(e){__bridge=null;__diag.errs.push('bridge:'+String(e));}

var __plan=null;
if(__bridge&&__lexTok&&typeof __bridge.GetDocumentPlanJson==='function'){
  try{
    var __rawPlan=__bridge.GetDocumentPlanJson(__lexTok,location.href||'',__parentHref,__isTop);
    if(__rawPlan){__plan=JSON.parse(__rawPlan);__diag.planOk=true;__diag.enabled=__plan.enabled!==false;}
  }catch(e){__plan=null;__diag.errs.push('plan:'+String(e));}
}
if(__plan){
  if(__plan.enabled===false)return;
  if(__plan.host)__effectiveHost=__plan.host;
  if(__plan.cosmetic===false)__genericAllowed=false;
  __diag.cosmetic=__genericAllowed;
  if(__plan.ghide){window.__lexBraveGenericHide=true;__diag.ghide=true;}
  if(__plan.scriptlets){
    __diag.scriptletLen=__plan.scriptlets.length;
    try{(new Function(__plan.scriptlets))();__diag.scriptletsOk=true;}catch(e){__diag.errs.push('scriptlet:'+String(e));}
  }
  if(__plan.noPopup){__installNoPopup();__diag.noPopup=true;}
  if(__plan.hostCss){__ensureStyle('lex-brave-host',__plan.hostCss);__diag.hostCss=true;}
  if(__plan.styleCss){__ensureStyle('lex-brave-style',__plan.styleCss);__diag.styleCss=true;}
  if(__plan.mgCss&&!__plan.ghide&&__genericAllowed){__ensureStyle('lex-brave-mg',__plan.mgCss);__diag.mgCss=true;}
  if(__plan.csp&&__plan.csp.length){
    __pendingCsp=__plan.csp.slice();
    __flushCsp();
  }
  if(__plan.proc&&__plan.proc.length){
    window.__lexBravePendingProcedural=(window.__lexBravePendingProcedural||[]).concat(__plan.proc);
  }
  if(__plan.ovl&&!__plan.ghide){__enableOverlayDefuser=true;__diag.ovl=true;}
}

function __parseMatcher(arg){
  var t=(arg||'').trim();
  if(t.length>=2&&t[0]==='/'){
    var last=t.lastIndexOf('/');
    if(last>0){
      try{return new RegExp(t.slice(1,last),t.slice(last+1));}catch(_){}
    }
  }
  return t;
}
function __matchText(val,m){
  var s=(val==null?'':String(val));
  if(m instanceof RegExp)return m.test(s);
  return s.indexOf(m)!==-1;
}
function __splitSteps(rule){
  var ops=[':has-text(',':has(',':not(',':is(',':matches-css-before(',':matches-css-after(',':matches-css(',':matches-attr(',':matches-path(',':matches-media(',':min-text-length(',':upward(',':xpath(',':watch-attr(',':remove(',':remove-attr(',':remove-class(',':style('];
  var i=0,n=rule.length,parts=[];
  function scanParen(startOpen){
    var d=0,q=null;
    for(var k=startOpen;k<n;k++){
      var c=rule[k];
      if(q){if(c==='\\'&&k+1<n){k++;continue;}if(c===q)q=null;continue;}
      if(c==='"'||c==="'")q=c;
      else if(c==='(')d++;
      else if(c===')'){d--;if(d===0)return k;}
    }
    return n-1;
  }
  var cssBuf='',depth=0,q=null;
  while(i<n){
    var ch=rule[i];
    if(q){cssBuf+=ch;if(ch==='\\'&&i+1<n){i++;cssBuf+=rule[i];}else if(ch===q)q=null;i++;continue;}
    if(ch==='"'||ch==="'"){q=ch;cssBuf+=ch;i++;continue;}
    if(ch==='('){depth++;cssBuf+=ch;i++;continue;}
    if(ch===')'){depth=Math.max(0,depth-1);cssBuf+=ch;i++;continue;}
    if(depth===0&&ch===':'){
      var matchedOp=null;
      for(var oi=0;oi<ops.length;oi++){
        if(rule.startsWith(ops[oi],i)){matchedOp=ops[oi];break;}
      }
      if(matchedOp){
        // :has / :not / :is sólo salen de CSS nativo si contienen algún operador procedural adentro
        var closeIdx=scanParen(i+matchedOp.length-1);
        var arg=rule.slice(i+matchedOp.length,closeIdx);
        var isNativePseudo=(matchedOp===':has('||matchedOp===':not('||matchedOp===':is(');
        var hasInnerProc=isNativePseudo&&/:(has-text|matches-|min-text-length|upward|xpath|style|remove)/.test(arg);
        if(isNativePseudo&&!hasInnerProc){
          cssBuf+=rule.slice(i,closeIdx+1);
          i=closeIdx+1;
          continue;
        }
        if(cssBuf.trim())parts.push({k:'css',v:cssBuf.trim()});
        cssBuf='';
        parts.push({k:'op',op:matchedOp.slice(1,-1),arg:arg});
        i=closeIdx+1;
        continue;
      }
    }
    cssBuf+=ch;
    i++;
  }
  if(cssBuf.trim())parts.push({k:'css',v:cssBuf.trim()});
  return parts;
}
function __safeQsa(root,sel){
  try{return Array.from((root||document).querySelectorAll(sel));}catch(_){return [];}
}
function __applyCombinator(els,sel){
  var s=sel.trim(),comb=' ';
  if(s[0]==='>'||s[0]==='+'||s[0]==='~'){comb=s[0];s=s.slice(1).trim();}
  var out=[],seen=new Set();
  for(var i=0;i<els.length;i++){
    var el=els[i],list=[];
    if(comb===' '){list=__safeQsa(el,s);}
    else if(comb==='>'){
      for(var c=el.firstElementChild;c;c=c.nextElementSibling){try{if(!s||c.matches(s))list.push(c);}catch(_){}}
    }else if(comb==='+'){
      var nx=el.nextElementSibling;if(nx){try{if(!s||nx.matches(s))list.push(nx);}catch(_){}}
    }else if(comb==='~'){
      var nx2=el.nextElementSibling;while(nx2){try{if(!s||nx2.matches(s))list.push(nx2);}catch(_){}nx2=nx2.nextElementSibling;}
    }
    for(var j=0;j<list.length;j++){if(!seen.has(list[j])){seen.add(list[j]);out.push(list[j]);}}
  }
  return out;
}
function __evalSteps(steps,seed){
  var els=seed||null;
  for(var i=0;i<steps.length;i++){
    var st=steps[i];
    if(st.k==='css'){
      if(els===null){els=__safeQsa(document,st.v);}
      else{els=__applyCombinator(els,st.v);}
      if(!els.length)return [];
      continue;
    }
    var op=st.op,arg=st.arg;
    if(op==='matches-path'){
      var pm=__parseMatcher(arg);
      if(!__matchText(location.pathname+location.search,pm))return [];
      continue;
    }
    if(op==='matches-media'){
      try{if(!window.matchMedia(arg).matches)return [];}catch(_){return [];}
      continue;
    }
    if(op==='xpath'){
      var roots=els===null?[document]:els,xout=[],xseen=new Set();
      for(var r=0;r<roots.length;r++){
        try{
          var snap=document.evaluate(arg,roots[r],null,XPathResult.ORDERED_NODE_SNAPSHOT_TYPE,null);
          for(var xi=0;xi<snap.snapshotLength;xi++){
            var node=snap.snapshotItem(xi);
            if(node&&node.nodeType===1&&!xseen.has(node)){xseen.add(node);xout.push(node);}
          }
        }catch(_){}
      }
      els=xout;
      if(!els.length)return [];
      continue;
    }
    if(els===null)els=__safeQsa(document,'*');
    if(op==='has-text'){
      var hm=__parseMatcher(arg);
      els=els.filter(function(el){return __matchText(el.textContent||'',hm);});
    }else if(op==='min-text-length'){
      var minL=parseInt(arg,10)||0;
      els=els.filter(function(el){return (el.textContent||'').length>=minL;});
    }else if(op==='matches-css'||op==='matches-css-before'||op==='matches-css-after'){
      var colon=arg.indexOf(':');if(colon<0){els=[];break;}
      var prop=arg.slice(0,colon).trim(),cm=__parseMatcher(arg.slice(colon+1).trim());
      var pseudo=op==='matches-css-before'?'::before':(op==='matches-css-after'?'::after':null);
      els=els.filter(function(el){
        try{
          var val=getComputedStyle(el,pseudo).getPropertyValue(prop);
          if(pseudo&&typeof val==='string'&&val.length>=2&&val[0]==='"'&&val[val.length-1]==='"')val=val.slice(1,-1);
          return __matchText(val,cm);
        }catch(_){return false;}
      });
    }else if(op==='matches-attr'){
      var eq=arg.indexOf('='),an=eq>=0?arg.slice(0,eq).trim():arg.trim(),av=eq>=0?arg.slice(eq+1).trim():null;
      if(av&&av.length>=2&&((av[0]==='"'&&av[av.length-1]==='"')||(av[0]==="'"&&av[av.length-1]==="'")))av=av.slice(1,-1);
      var am=av!==null?__parseMatcher(av):null,nm=__parseMatcher(an);
      els=els.filter(function(el){
        if(!el.attributes)return false;
        for(var ai=0;ai<el.attributes.length;ai++){
          var at=el.attributes[ai];
          if((nm instanceof RegExp?nm.test(at.name):at.name===nm)&&(am===null||__matchText(at.value,am)))return true;
        }
        return false;
      });
    }else if(op==='has'){
      var subSteps=__splitSteps(arg);
      els=els.filter(function(el){
        if(subSteps.length&&subSteps[0].k==='css'){
          var firstComb=__applyCombinator([el],subSteps[0].v);
          if(!firstComb.length)return false;
          return __evalSteps(subSteps.slice(1),firstComb).length>0;
        }
        return __evalSteps(subSteps,[el]).length>0;
      });
    }else if(op==='not'){
      var notSub=__splitSteps(arg);
      els=els.filter(function(el){
        var clone=notSub.slice();
        if(clone.length&&clone[0].k==='css'){
          try{if(!el.matches(clone[0].v))return true;}catch(_){return true;}
          return __evalSteps(clone.slice(1),[el]).length===0;
        }
        return __evalSteps(clone,[el]).length===0;
      });
    }else if(op==='is'){
      var subs=arg.split(','),isOut=[];
      els=els.filter(function(el){
        for(var si=0;si<subs.length;si++){if(__evalSteps(__splitSteps(subs[si]),[el]).length>0)return true;}
        return false;
      });
    }else if(op==='upward'){
      var uOut=[],uSeen=new Set(),nUp=/^\d+$/.test(arg.trim())?parseInt(arg.trim(),10):0;
      for(var ui=0;ui<els.length;ui++){
        var cur=els[ui];
        if(nUp>0){
          for(var step=0;step<nUp&&cur;step++)cur=cur.parentElement;
        }else{
          cur=cur.parentElement;
          while(cur){try{if(cur.matches(arg))break;}catch(_){cur=null;break;}cur=cur.parentElement;}
        }
        if(cur&&!uSeen.has(cur)){uSeen.add(cur);uOut.push(cur);}
      }
      els=uOut;
    }else if(op==='watch-attr'){
      // Continuar al siguiente paso sobre los elementos actuales
    }else if(op==='remove'||op==='remove-attr'||op==='remove-class'||op==='style'){
      // Acción terminal manejada fuera del filtro
      break;
    }
    if(!els.length)return [];
  }
  return els||[];
}
var __compiledProc=[];
function __compileProcedural(){
  var pending=window.__lexBravePendingProcedural;
  if(pending&&pending.length){
    window.__lexBravePendingProcedural=[];
    for(var i=0;i<pending.length;i++){
      var r=pending[i],steps=__splitSteps(r);
      if(!steps.length)continue;
      var last=steps[steps.length-1],action=null;
      if(last.k==='op'&&(last.op==='remove'||last.op==='remove-attr'||last.op==='remove-class'||last.op==='style')){
        action=last;steps=steps.slice(0,-1);
      }
      __compiledProc.push({steps:steps,action:action});
    }
  }
}
window.__lexBraveApplyProcedural=function(){
  __compileProcedural();
  if(!__compiledProc.length||!document.documentElement)return;
  var hits=0;
  for(var i=0;i<__compiledProc.length;i++){
    var item=__compiledProc[i];
    var targets=__evalSteps(item.steps,null);
    for(var j=0;j<targets.length;j++){
      var el=targets[j];
      if(!el||!el.isConnected)continue;
      var counted=__countedEls.has(el);
      if(!item.action){
        el.style.setProperty('display','none','important');
      }else if(item.action.op==='remove'){
        el.remove();
      }else if(item.action.op==='style'){
        if(!el.getAttribute('data-lex-proc-styled')){
          el.setAttribute('style',(el.getAttribute('style')||'')+';'+item.action.arg);
          el.setAttribute('data-lex-proc-styled','1');
        }
      }else if(item.action.op==='remove-attr'){
        var am=__parseMatcher(item.action.arg);
        if(am instanceof RegExp){
          var names=[];for(var ai=0;ai<el.attributes.length;ai++){if(am.test(el.attributes[ai].name))names.push(el.attributes[ai].name);}
          for(var ni=0;ni<names.length;ni++)el.removeAttribute(names[ni]);
        }else if(el.hasAttribute(am)){el.removeAttribute(am);}
      }else if(item.action.op==='remove-class'){
        var cm=__parseMatcher(item.action.arg);
        if(cm instanceof RegExp){
          var rem=[];el.classList.forEach(function(cn){if(cm.test(cn))rem.push(cn);});
          for(var ri=0;ri<rem.length;ri++)el.classList.remove(rem[ri]);
        }else{el.classList.remove(cm);}
      }
      if(!counted){__countedEls.add(el);hits++;}
    }
  }
  if(hits>0)__reportCosmetic(hits,'brave_procedural');
};

function __collectTokens(node,budget){
  if(!node||node.nodeType!==1||budget.n<=0)return;
  if(node.id&&!__seenIds.has(node.id)){__seenIds.add(node.id);__pendIds.push(node.id);}
  var cl=node.classList;
  if(cl&&cl.length){for(var i=0;i<cl.length;i++){var c=cl[i];if(c&&!__seenCls.has(c)){__seenCls.add(c);__pendCls.push(c);}}}
  budget.n--;
  var ch=node.firstElementChild;
  while(ch&&budget.n>0){__collectTokens(ch,budget);ch=ch.nextElementSibling;}
}
function __drainBounded(src,budget){
  var out=[],chars=0;
  while(src.length>0&&out.length<80){
    var s=src[0],add=s.length+4;
    if(out.length>0&&chars+add>budget)break;
    src.shift();
    out.push(s);
    chars+=add;
  }
  return out;
}
function __countHideCssHits(cssText,reason){
  if(!cssText)return;
  var blocks=cssText.split(/\{display:none!important;\}\s*/);
  var newHits=0;
  for(var bi=0;bi<blocks.length;bi++){
    var sel=blocks[bi].trim();if(!sel)continue;
    var matched=__safeQsa(document,sel);
    for(var mi=0;mi<matched.length;mi++){
      if(!__countedEls.has(matched[mi])){__countedEls.add(matched[mi]);newHits++;}
    }
  }
  if(newHits>0)__reportCosmetic(newHits,reason);
}
function __applyGenericReply(d){
  if(!d||d.type!=='brave_cosmetic_apply')return;
  if(d.host&&d.host!==__effectiveHost)return;
  if(d.ghide){
    window.__lexBraveGenericHide=true;
    __removeStyle('lex-brave-gen');
    __removeStyle('lex-brave-mg');
    __pendCls.length=0;__pendIds.length=0;
    return;
  }
  if(d.mgCss)__ensureStyle('lex-brave-mg',d.mgCss);
  if(d.css){
    __appendStyle('lex-brave-gen',d.css);
    __countHideCssHits(d.css,'brave_generic_cosmetic');
  }
}
function __flushGeneric(){
  __scanTimer=0;
  if(!__genericAllowed||window.__lexBraveGenericHide===true)return;
  if(__initialSent&&__pendCls.length===0&&__pendIds.length===0)return;
  if(__bridge&&__lexTok&&typeof __bridge.QueryCosmeticsJson==='function'){
    do{
      var clsBatch=__drainBounded(__pendCls,1400);
      var idBatch=__drainBounded(__pendIds,1000);
      var isInit=!__initialSent;
      __initialSent=true;
      try{
        var raw=__bridge.QueryCosmeticsJson(__lexTok,location.href||'',__parentHref,__isTop,isInit,JSON.stringify(clsBatch),JSON.stringify(idBatch));
        if(raw)__applyGenericReply(JSON.parse(raw));
      }catch(e){__diag.errs.push('cos:'+String(e));break;}
    }while(__pendCls.length>0||__pendIds.length>0);
    return;
  }
  if(!__isTop||!window.chrome||!window.chrome.webview||typeof window.chrome.webview.postMessage!=='function')return;
  do{
    var clsB=__drainBounded(__pendCls,1400);
    var idB=__drainBounded(__pendIds,1000);
    var isIni=!__initialSent;
    __initialSent=true;
    try{
      window.chrome.webview.postMessage({
        type:'brave_cosmetic_query',
        host:__effectiveHost,
        initial:isIni,
        classes:clsB,
        ids:idB
      });
    }catch(e){__diag.errs.push('pm:'+String(e));break;}
  }while(__pendCls.length>0||__pendIds.length>0);
}
function __scheduleScan(){
  if(__genericAllowed&&!window.__lexBraveGenericHide&&!__scanTimer){
    __scanTimer=setTimeout(__flushGeneric,28);
  }
  if(!__procTimer&&(__compiledProc.length>0||(window.__lexBravePendingProcedural&&window.__lexBravePendingProcedural.length>0))){
    __procTimer=setTimeout(function(){__procTimer=0;try{window.__lexBraveApplyProcedural();}catch(_){}},60);
  }
}

if(!(__bridge&&__lexTok)&&__isTop&&window.chrome&&window.chrome.webview&&typeof window.chrome.webview.addEventListener==='function'){
  window.chrome.webview.addEventListener('message',function(ev){
    __applyGenericReply(ev&&ev.data);
  });
}

function __reapplyCachedStyles(){
  var root=document.head||document.documentElement;
  if(!root)return;
  var pKeys=Object.keys(__pendingStyles);
  for(var i=0;i<pKeys.length;i++){
    var pk=pKeys[i];
    __applyStyleEl(pk,__pendingStyles[pk],root);
  }
  var aKeys=Object.keys(__styleTexts);
  for(var j=0;j<aKeys.length;j++){
    var ak=aKeys[j],el=document.getElementById(ak);
    if(!el||el.textContent!==__styleTexts[ak]){
      __applyStyleEl(ak,__styleTexts[ak],root);
    }
  }
}

function __defuseClickjackTarget(el){
  if(!__enableOverlayDefuser||window.__lexBraveGenericHide||!el||el.nodeType!==1)return false;
  var tag=(el.tagName||'').toUpperCase();
  if(tag!=='DIV'&&tag!=='A'&&tag!=='SPAN'&&tag!=='SECTION')return false;
  if(el.childElementCount!==0)return false;
  if((el.textContent||'').trim()!=='')return false;
  try{
    var cs=getComputedStyle(el);
    if(!cs)return false;
    var pos=cs.position;
    if(pos!=='fixed'&&pos!=='absolute')return false;
    var z=parseInt(cs.zIndex,10);
    if(!(z>=9999))return false;
    var bg=cs.backgroundColor||'';
    var isInvisible=(cs.opacity==='0'||bg==='transparent'||bg==='rgba(0, 0, 0, 0)');
    if(!isInvisible)return false;
    var rect=el.getBoundingClientRect();
    var vw=window.innerWidth||0,vh=window.innerHeight||0;
    if(vw<100||vh<100)return false;
    if(rect.width>=vw*0.85&&rect.height>=vh*0.85){
      el.style.setProperty('display','none','important');
      el.style.setProperty('pointer-events','none','important');
      __reportShield('overlay',1,'brave_clickjack_overlay');
      return true;
    }
  }catch(_){}
  return false;
}

if(__enableOverlayDefuser&&typeof document.addEventListener==='function'){
  try{
    document.addEventListener('mousedown',function(ev){
      if(!__enableOverlayDefuser||window.__lexBraveGenericHide)return;
      var hit=ev&&ev.target;
      if(__defuseClickjackTarget(hit)){ev.preventDefault();ev.stopPropagation();}
    },true);
  }catch(_){}
}

function __onDomStart(){
  __flushCsp();
  __reapplyCachedStyles();
  if(__genericAllowed&&document.documentElement)__collectTokens(document.documentElement,{n:2000});
  if(__plan&&__plan.proc&&__plan.proc.length&&document.documentElement){
    try{window.__lexBraveApplyProcedural();}catch(_){}
  }
  __scheduleScan();
  try{
    var mo=new MutationObserver(function(muts){
      __flushCsp();
      __reapplyCachedStyles();
      if(__genericAllowed&&!window.__lexBraveGenericHide){
        var budget={n:800};
        for(var i=0;i<muts.length&&budget.n>0;i++){
          var m=muts[i];
          if(m.type==='attributes'&&m.target&&m.target.nodeType===1){
            var t=m.target;
            if(t.id&&!__seenIds.has(t.id)){__seenIds.add(t.id);__pendIds.push(t.id);}
            if(t.classList){for(var ci=0;ci<t.classList.length;ci++){var cn=t.classList[ci];if(cn&&!__seenCls.has(cn)){__seenCls.add(cn);__pendCls.push(cn);}}}
          }else if(m.addedNodes){
            for(var j=0;j<m.addedNodes.length&&budget.n>0;j++)__collectTokens(m.addedNodes[j],budget);
          }
        }
      }
      __scheduleScan();
    });
    mo.observe(document.documentElement||document,{childList:true,subtree:true,attributes:true,attributeFilter:['class','id']});
  }catch(_){}
}

__onDomStart();
if(document.readyState==='loading'){
  document.addEventListener('DOMContentLoaded',function(){
    __flushCsp();
    __reapplyCachedStyles();
    if(__genericAllowed&&document.documentElement)__collectTokens(document.documentElement,{n:2000});
    if(__genericAllowed&&!window.__lexBraveGenericHide)__flushGeneric();
    if(__plan&&__plan.hostCss)__countHideCssHits(__plan.hostCss,'brave_host_cosmetic');
    try{window.__lexBraveApplyProcedural();}catch(_){}
  },{once:true});
}else{
  if(__genericAllowed&&!window.__lexBraveGenericHide)__flushGeneric();
  if(__plan&&__plan.hostCss)__countHideCssHits(__plan.hostCss,'brave_host_cosmetic');
}
})();
""";
}
