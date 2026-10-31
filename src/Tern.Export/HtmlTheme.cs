namespace Tern.Export;

/// <summary>The stylesheet embedded in every exported page. It uses system fonts only and has light and dark variants.</summary>
internal static class HtmlTheme
{
    public const string Css = """
:root{color-scheme:light;--bg:#ffffff;--fg:#1f2328;--muted:#59636e;--border:#d1d9e0;--soft:#f6f8fa;--link:#0969da;--code-bg:#eff1f3;--mark:#fff8c5;--accent:#0969da;--quote:#d1d9e0;--note:#0969da;--tip:#1a7f37;--important:#8250df;--warning:#9a6700;--caution:#cf222e;--node-fill:#eef4ff;--node-stroke:#4a6fa5;--edge:#4b5563;--frame:#9ca3af}
:root[data-theme=dark]{color-scheme:dark;--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--border:#3d444d;--soft:#151b23;--link:#4493f8;--code-bg:#262c36;--mark:#5a4a00;--accent:#4493f8;--quote:#3d444d;--note:#4493f8;--tip:#3fb950;--important:#ab7df8;--warning:#d29922;--caution:#f85149;--node-fill:#1c2a44;--node-stroke:#6b92d1;--edge:#a0a8b3;--frame:#6b7280}
@media (prefers-color-scheme:dark){:root[data-theme=auto]{color-scheme:dark;--bg:#0d1117;--fg:#e6edf3;--muted:#9198a1;--border:#3d444d;--soft:#151b23;--link:#4493f8;--code-bg:#262c36;--mark:#5a4a00;--accent:#4493f8;--quote:#3d444d;--note:#4493f8;--tip:#3fb950;--important:#ab7df8;--warning:#d29922;--caution:#f85149;--node-fill:#1c2a44;--node-stroke:#6b92d1;--edge:#a0a8b3;--frame:#6b7280}}
:root[data-theme=auto]{color-scheme:light dark}
*{box-sizing:border-box}
html{-webkit-text-size-adjust:100%}
body{margin:0;background:var(--bg);color:var(--fg);font:16px/1.6 system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,"Noto Sans",sans-serif}
.markdown-body{max-width:50rem;margin:0 auto;padding:2rem 1.25rem 4rem;overflow-wrap:break-word}
.markdown-body>:first-child{margin-top:0}
h1,h2,h3,h4,h5,h6{margin:1.6em 0 .6em;line-height:1.25;font-weight:600}
h1{font-size:2em;padding-bottom:.3em;border-bottom:1px solid var(--border)}
h2{font-size:1.5em;padding-bottom:.3em;border-bottom:1px solid var(--border)}
h3{font-size:1.25em}h4{font-size:1em}h5{font-size:.875em}h6{font-size:.85em;color:var(--muted)}
p,ul,ol,dl,blockquote,pre,table,details,figure,.alert,.math-block,.diagram-block{margin:0 0 1em}
a{color:var(--link);text-decoration:none}
a:hover{text-decoration:underline}
ul,ol{padding-left:2em}
li+li{margin-top:.25em}
li>p{margin:.5em 0}
li.task{list-style:none;margin-left:-1.4em}
li.task .box{display:inline-block;width:1.4em;color:var(--muted)}
li.task.done .box{color:var(--tip)}
blockquote{padding:0 1em;color:var(--muted);border-left:.25em solid var(--quote)}
blockquote>:last-child{margin-bottom:0}
code,kbd,samp{font:.875em/1.4 ui-monospace,SFMono-Regular,Consolas,"Liberation Mono",Menlo,monospace}
code{padding:.15em .35em;background:var(--code-bg);border-radius:4px}
pre{padding:1em;overflow:auto;background:var(--soft);border:1px solid var(--border);border-radius:6px;line-height:1.45}
pre code{padding:0;background:none;font-size:.85em}
kbd{padding:.15em .4em;border:1px solid var(--border);border-bottom-width:2px;border-radius:4px;background:var(--soft)}
mark{background:var(--mark);color:inherit;padding:0 .15em}
hr{height:1px;margin:1.5em 0;border:0;background:var(--border)}
table{border-collapse:collapse;display:block;max-width:100%;overflow:auto}
th,td{padding:.4em .8em;border:1px solid var(--border)}
th{background:var(--soft);font-weight:600}
tr:nth-child(2n) td{background:var(--soft)}
.a-left{text-align:left}.a-center{text-align:center}.a-right{text-align:right}
img{max-width:100%;height:auto}
.image-placeholder{display:inline-block;padding:.2em .6em;color:var(--muted);border:1px dashed var(--border);border-radius:4px;font-size:.9em}
.link-local,.link-blocked{border-bottom:1px dotted var(--muted)}
.alert{padding:.6em 1em;border-left:.25em solid var(--note);background:var(--soft);border-radius:0 6px 6px 0}
.alert>:last-child{margin-bottom:0}
.alert-title{margin:0 0 .4em;font-weight:600;color:var(--note)}
.alert-tip{border-left-color:var(--tip)}.alert-tip .alert-title{color:var(--tip)}
.alert-important{border-left-color:var(--important)}.alert-important .alert-title{color:var(--important)}
.alert-warning{border-left-color:var(--warning)}.alert-warning .alert-title{color:var(--warning)}
.alert-caution{border-left-color:var(--caution)}.alert-caution .alert-title{color:var(--caution)}
.math-block{text-align:center;font-size:1.15em;font-family:"Cambria Math","STIX Two Math",Cambria,"Times New Roman",serif;overflow-x:auto}
.math{font-family:"Cambria Math","STIX Two Math",Cambria,"Times New Roman",serif}
.math-error{color:var(--caution)}
.footnotes{margin-top:2.5em;padding-top:.5em;border-top:1px solid var(--border);font-size:.9em;color:var(--muted)}
.footnote-ref{font-size:.75em;line-height:0}
details{padding:.4em .8em;border:1px solid var(--border);border-radius:6px}
summary{cursor:pointer;font-weight:600}
.front-matter dl{display:grid;grid-template-columns:max-content 1fr;gap:.2em 1em;margin:.6em 0 0}
.front-matter dt{font-weight:600;color:var(--muted)}.front-matter dd{margin:0}
.diagram-block{overflow-x:auto;text-align:center}
svg.diagram{max-width:100%;height:auto}
.d-node{fill:var(--node-fill);stroke:var(--node-stroke);stroke-width:1.5}
.d-sub{fill:none;stroke:var(--frame);stroke-width:1;stroke-dasharray:6 4}
.d-text{fill:var(--fg);font:14px system-ui,"Segoe UI",Arial,sans-serif}
.d-title{fill:var(--muted);font:bold 12px system-ui,"Segoe UI",Arial,sans-serif}
.d-edge{fill:none;stroke:var(--edge);stroke-width:1.5}
.d-label-leader{fill:none;stroke:var(--edge);stroke-width:.8;stroke-dasharray:2 3}
.d-dotted{stroke-dasharray:5 4}
.d-thick{stroke-width:3}
.d-head{fill:var(--edge);stroke:var(--edge);stroke-width:1}
.d-head-open{fill:var(--bg);stroke:var(--edge);stroke-width:1.5}
.d-label-bg{fill:var(--bg);stroke:none;opacity:.92}
.d-life{stroke:var(--frame);stroke-width:1;stroke-dasharray:4 4}
.d-note{fill:var(--mark);stroke:var(--frame);stroke-width:1}
.d-frame{fill:none;stroke:var(--frame);stroke-width:1.2}
.notice{margin:.4em 0 0;color:var(--muted);font-size:.9em;font-style:italic}
.truncated{padding:.6em 1em;border:1px solid var(--warning);border-radius:6px;color:var(--warning)}
@media print{body{background:#fff;color:#000}.markdown-body{max-width:none;padding:0}pre,table,blockquote,.alert,svg.diagram{break-inside:avoid}a{color:inherit}}
""";
}
