"""Markdown <-> HTML helpers for work-item descriptions and Task titles.

Azure DevOps stores ``System.Description`` as HTML, so backlog Markdown is
converted on the way in; ``norm``/``plain`` reverse it for stable comparisons.
"""
import re


def escape_html(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


SENTINEL = "\x00%d\x00"


def _protect_code(text):
    """Lift `code` spans out so the bold/italic passes cannot reach inside them.

    A backlog says things like ``SharedKernel.*`` and ``*Command``. Left in
    place, the italics pass pairs those asterisks across the spans and emits
    interleaved <i>/<code> tags that no browser can nest.
    """
    spans = []

    def stash(m):
        spans.append(m.group(1))
        return SENTINEL % (len(spans) - 1)

    return re.sub(r"`([^`]+)`", stash, text), spans


def _restore_code(text, spans):
    return re.sub(r"\x00(\d+)\x00", lambda m: f"<code>{spans[int(m.group(1))]}</code>", text)


def replace_inline_markdown(text):
    text, spans = _protect_code(text)
    # **bold** -> <b>bold</b>
    text = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", text)
    # *italics* -> <i>italics</i>
    text = re.sub(r"\*([^*]+)\*", r"<i>\1</i>", text)
    return _restore_code(text, spans)


BULLET = re.compile(r"^(\s*)[-*+]\s+(.*)$")
TABLE_ROW = re.compile(r"^\s*\|.*\|\s*$")
TABLE_SEP = re.compile(r"^\s*\|[\s:|-]+\|\s*$")
RULE = re.compile(r"^\s*(-{3,}|\*{3,}|_{3,})\s*$")

# Azure DevOps renders System.Description as raw HTML with no stylesheet of its
# own, so a bare <table> arrives without borders. The styles ride inline.
TABLE_STYLE = "border-collapse:collapse;margin:8px 0;"
CELL_STYLE = "border:1px solid #c8c8c8;padding:4px 8px;text-align:left;vertical-align:top;"
HEAD_STYLE = CELL_STYLE + "background:#f4f4f4;font-weight:600;"


def _fmt(text):
    return replace_inline_markdown(escape_html(text))


def _cells(row):
    return [c.strip() for c in row.strip().strip("|").split("|")]


def _fold(lines):
    """Group raw lines into blocks, joining wrapped continuation lines.

    A backlog wraps prose across source lines. Each such line is part of the
    bullet or paragraph above it, so it is joined rather than made a block of
    its own. A blank line ends the block, which is what separates them.
    """
    blocks = []
    for raw in lines:
        line = raw.rstrip()
        stripped = line.strip()

        if not stripped:
            blocks.append(["blank", None])
        elif RULE.match(line):
            blocks.append(["rule", None])
        elif TABLE_ROW.match(line):
            blocks.append(["row", stripped])
        elif BULLET.match(line):
            m = BULLET.match(line)
            blocks.append(["bullet", [len(m.group(1)), m.group(2).strip()]])
        elif blocks and blocks[-1][0] == "bullet":
            blocks[-1][1][1] += " " + stripped
        elif blocks and blocks[-1][0] == "text":
            blocks[-1][1] += " " + stripped
        else:
            blocks.append(["text", stripped])
    return blocks


def markdown_to_html(lines):
    """Convert markdown lines (bullets, tables, bold, italics, code) to HTML."""
    out = []
    stack = []   # indent of each open <ul>
    table = []

    def close_li():
        if out:
            out[-1] += "</li>"

    def close_lists():
        while stack:
            close_li()
            out.append("</ul>")
            stack.pop()

    def flush_table():
        rows = [r for r in table if not TABLE_SEP.match(r)]
        table.clear()
        if not rows:
            return
        out.append(f'<table style="{TABLE_STYLE}">')
        head = "".join(f'<th style="{HEAD_STYLE}">{_fmt(c)}</th>' for c in _cells(rows[0]))
        out.append(f"<tr>{head}</tr>")
        for row in rows[1:]:
            body = "".join(f'<td style="{CELL_STYLE}">{_fmt(c)}</td>' for c in _cells(row))
            out.append(f"<tr>{body}</tr>")
        out.append("</table>")

    for kind, payload in _fold(lines):
        if kind == "row":
            close_lists()
            table.append(payload)
            continue
        flush_table()

        if kind == "blank":
            continue
        if kind == "rule":
            close_lists()
            out.append("<hr>")
        elif kind == "bullet":
            indent, text = payload
            if not stack or indent > stack[-1]:
                out.append("<ul>")          # a deeper level nests in the open <li>
                stack.append(indent)
            else:
                while len(stack) > 1 and indent < stack[-1]:
                    close_li()
                    out.append("</ul>")
                    stack.pop()
                close_li()                  # close the previous sibling
            out.append(f"<li>{_fmt(text)}")
        else:
            close_lists()
            out.append(f"<p>{_fmt(payload)}</p>")

    flush_table()
    close_lists()
    return "\n".join(out)


def inline(t):
    """Minimal inline markdown -> HTML used for Task descriptions."""
    t = t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    t, spans = _protect_code(t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", t)
    return _restore_code(t, spans)


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
