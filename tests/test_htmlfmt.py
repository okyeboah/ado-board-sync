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

    def test_table_becomes_html_table(self):
        html = htmlfmt.markdown_to_html(["| Route | Owner |", "|---|---|", "| `GET /x` | DDI-1 |"])
        self.assertIn("<table", html)
        self.assertIn("<th", html)
        self.assertIn("<code>GET /x</code>", html)
        self.assertNotIn("|", html)          # no raw pipe survives
        self.assertEqual(html.count("<tr>"), 2)   # the |---| separator is dropped

    def test_nested_bullet_nests_inside_the_parent_item(self):
        html = htmlfmt.markdown_to_html(["- parent", "  * child", "- sibling"])
        self.assertEqual(
            html,
            "<ul>\n<li>parent\n<ul>\n<li>child</li>\n</ul></li>\n<li>sibling</li>\n</ul>",
        )

    def test_wrapped_line_joins_the_bullet_above_it(self):
        html = htmlfmt.markdown_to_html(["- first half", "  second half", "- next"])
        self.assertEqual(html, "<ul>\n<li>first half second half</li>\n<li>next</li>\n</ul>")

    def test_wrapped_line_joins_the_paragraph_above_it(self):
        self.assertEqual(htmlfmt.markdown_to_html(["one", "two"]), "<p>one two</p>")

    def test_blank_line_separates_paragraphs(self):
        self.assertEqual(htmlfmt.markdown_to_html(["one", "", "two"]), "<p>one</p>\n<p>two</p>")

    def test_asterisk_inside_a_code_span_is_not_italics(self):
        self.assertEqual(
            htmlfmt.markdown_to_html(["`SharedKernel.*` needs no `DDI.*` project"]),
            "<p><code>SharedKernel.*</code> needs no <code>DDI.*</code> project</p>",
        )

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
