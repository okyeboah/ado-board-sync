"""Markdown <-> HTML helpers for work-item descriptions and Task titles.

Azure DevOps stores ``System.Description`` as HTML, so backlog Markdown is
converted on the way in; ``norm``/``plain`` reverse it for stable comparisons.
"""
import re


def escape_html(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def replace_inline_markdown(text):
    # `code` -> <code>code</code>
    text = re.sub(r"`([^`]+)`", r"<code>\1</code>", text)
    # **bold** -> <b>bold</b>
    text = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", text)
    # *italics* -> <i>italics</i>
    text = re.sub(r"\*([^*]+)\*", r"<i>\1</i>", text)
    return text


def markdown_to_html(lines):
    """Convert a block of markdown lines (bullets, bold, italics, code) to HTML."""
    html_lines = []
    in_list = False

    for line in lines:
        line_str = line.strip()
        if not line_str:
            continue

        if line_str.startswith("-") or line_str.startswith("* "):
            if not in_list:
                html_lines.append("<ul>")
                in_list = True
            content = line_str.lstrip("-* ").strip()
            content = escape_html(content)
            content = replace_inline_markdown(content)
            html_lines.append(f"<li>{content}</li>")
        else:
            if in_list:
                html_lines.append("</ul>")
                in_list = False
            content = escape_html(line_str)
            content = replace_inline_markdown(content)
            html_lines.append(f"<p>{content}</p>")

    if in_list:
        html_lines.append("</ul>")

    return "\n".join(html_lines)


def inline(t):
    """Minimal inline markdown -> HTML used for Task descriptions."""
    t = t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    t = re.sub(r"`([^`]+)`", r"<code>\1</code>", t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", t)
    return t


def plain(t):
    """Strip inline markdown to plain text (used to build/compare Task titles)."""
    t = re.sub(r"`([^`]+)`", r"\1", t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"\1", t)
    return re.sub(r"\s+", " ", t).strip()


def norm(h):
    """Normalise HTML to plain lowercase text so we don't flag HTML artifacts."""
    h = h or ""
    h = re.sub(r"</?[^>]+>", " ", h)
    h = (
        h.replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&nbsp;", " ")
        .replace("&quot;", '"')
        .replace("&apos;", "'")
        .replace("&#39;", "'")
    )
    h = re.sub(r"\s+", " ", h)
    return h.strip().lower()
