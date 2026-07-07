import unittest

from ado_board_sync import htmlfmt


class HtmlFmtTest(unittest.TestCase):
    def test_bullets_become_list(self):
        html = htmlfmt.markdown_to_html(["- first", "- second"])
        self.assertEqual(html, "<ul>\n<li>first</li>\n<li>second</li>\n</ul>")

    def test_nested_star_bullet_is_flattened_list_item(self):
        html = htmlfmt.markdown_to_html(["  * detail"])
        self.assertEqual(html, "<ul>\n<li>detail</li>\n</ul>")

    def test_paragraph_with_inline_markup(self):
        html = htmlfmt.markdown_to_html(["Use `Result<T>` and **guard** the *edge*"])
        self.assertEqual(
            html,
            "<p>Use <code>Result&lt;T&gt;</code> and <b>guard</b> the <i>edge</i></p>",
        )

    def test_blank_lines_skipped(self):
        self.assertEqual(htmlfmt.markdown_to_html(["", "  ", "x"]), "<p>x</p>")

    def test_html_escaping(self):
        self.assertEqual(htmlfmt.markdown_to_html(["a & b < c"]), "<p>a &amp; b &lt; c</p>")

    def test_plain_strips_inline_markdown(self):
        self.assertEqual(
            htmlfmt.plain("Add **optimistic-concurrency** checks for `x`"),
            "Add optimistic-concurrency checks for x",
        )

    def test_norm_roundtrips_html_to_text(self):
        a = htmlfmt.markdown_to_html(["- one", "- two"])
        self.assertEqual(htmlfmt.norm(a), "one two")

    def test_inline_for_task_descriptions(self):
        self.assertEqual(htmlfmt.inline("a `b` **c** < d"), "a <code>b</code> <b>c</b> &lt; d")


if __name__ == "__main__":
    unittest.main()
