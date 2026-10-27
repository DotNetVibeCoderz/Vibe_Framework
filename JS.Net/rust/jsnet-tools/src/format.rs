//! Conservative, token-aware formatter: re-indents by bracket depth (with `case` bodies and leading-dot
//! continuations), trims trailing whitespace, collapses runs of blank lines and guarantees a final newline.
//! Content inside multi-line strings, templates and block comments is never touched.

use crate::lexer::{tokenize, Kind};

pub struct Options {
    pub indent: usize,
    pub max_blank_lines: usize,
}

impl Default for Options {
    fn default() -> Self {
        Options { indent: 4, max_blank_lines: 1 }
    }
}

pub fn format(src: &str, opts: &Options) -> String {
    let newline = if src.contains("\r\n") { "\r\n" } else { "\n" };
    let normalized = src.replace("\r\n", "\n");
    let tokens = tokenize(&normalized);

    // Per line: bracket depth at line start, whether the line starts inside a multi-line token,
    // and the first token on the line.
    let line_count = normalized.split('\n').count();
    let mut depth_at_start = vec![0i32; line_count + 1];
    let mut frozen = vec![false; line_count + 1];
    let mut first_tok: Vec<Option<(Kind, &str)>> = vec![None; line_count + 1];
    let mut case_depth = vec![0i32; line_count + 1];

    let mut depth: i32 = 0;
    // Stack of brace depths that currently have an open `case` label.
    let mut case_stack: Vec<i32> = Vec::new();
    let mut current_line = 1usize;
    let mut pending_case = false;

    for tok in &tokens {
        while current_line < tok.line {
            current_line += 1;
            depth_at_start[current_line] = depth;
            case_depth[current_line] = case_stack.len() as i32;
        }
        if first_tok[tok.line].is_none() {
            first_tok[tok.line] = Some((tok.kind, tok.text));
            // `case x:` / `default:` lines close the previous case body at this depth.
            if tok.kind == Kind::Ident && (tok.text == "case" || tok.text == "default") {
                if case_stack.last() == Some(&depth) {
                    case_stack.pop();
                    case_depth[tok.line] = case_stack.len() as i32;
                }
                pending_case = true;
            }
            // A `}` that closes a switch also ends its last case body.
            if tok.kind == Kind::Punct && tok.text == "}" && case_stack.last() == Some(&depth) {
                case_depth[tok.line] = case_stack.len() as i32 - 1;
            }
        }
        // Multi-line tokens freeze the lines they span (after the first).
        let newlines = tok.text.matches('\n').count();
        if newlines > 0 && matches!(tok.kind, Kind::Template | Kind::BlockComment | Kind::Str) {
            for l in tok.line + 1..=tok.line + newlines {
                if l <= line_count {
                    frozen[l] = true;
                }
            }
        }
        if tok.kind == Kind::Punct {
            match tok.text {
                "{" | "(" | "[" => depth += 1,
                "}" | ")" | "]" => {
                    if tok.text == "}" && case_stack.last() == Some(&depth) {
                        case_stack.pop();
                    }
                    depth = (depth - 1).max(0);
                }
                ":" if pending_case => {
                    case_stack.push(depth);
                    pending_case = false;
                }
                _ => {}
            }
        }
    }
    while current_line < line_count {
        current_line += 1;
        depth_at_start[current_line] = depth;
        case_depth[current_line] = case_stack.len() as i32;
    }

    let mut out = String::with_capacity(normalized.len() + normalized.len() / 8);
    let mut blank_run = 0usize;
    let lines: Vec<&str> = normalized.split('\n').collect();
    for (i, raw) in lines.iter().enumerate() {
        let line_no = i + 1;
        if frozen[line_no] {
            out.push_str(raw);
            out.push('\n');
            blank_run = 0;
            continue;
        }
        let trimmed = raw.trim();
        if trimmed.is_empty() {
            blank_run += 1;
            if blank_run <= opts.max_blank_lines && !out.is_empty() {
                out.push('\n');
            }
            continue;
        }
        blank_run = 0;
        let mut level = depth_at_start[line_no] + case_depth[line_no];
        if let Some((kind, text)) = first_tok[line_no] {
            if kind == Kind::Punct && (text == "}" || text == ")" || text == "]") {
                level -= 1;
            }
            if kind == Kind::Punct && (text == "." || text == "?.") {
                level += 1;
            }
        }
        let level = level.max(0) as usize;
        out.push_str(&" ".repeat(level * opts.indent));
        out.push_str(trimmed);
        out.push('\n');
    }
    while out.ends_with("\n\n") {
        out.pop();
    }
    if !out.ends_with('\n') {
        out.push('\n');
    }
    if newline == "\r\n" {
        out = out.replace('\n', "\r\n");
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reindents_blocks_and_cases() {
        let src = "class A {\nfoo() {\nswitch (x) {\ncase 1:\nbar();\nbreak;\ndefault:\nbaz();\n}\n}\n}\n";
        let expected = "class A {\n    foo() {\n        switch (x) {\n            case 1:\n                bar();\n                break;\n            default:\n                baz();\n        }\n    }\n}\n";
        assert_eq!(format(src, &Options::default()), expected);
    }

    #[test]
    fn keeps_template_content() {
        let src = "const s = `a\n   keep  \nb`;\n";
        assert_eq!(format(src, &Options::default()), src);
    }

    #[test]
    fn indents_leading_dot_chains() {
        let src = "xs\n.map(x => x)\n.filter(x => x);\n";
        assert_eq!(format(src, &Options::default()), "xs\n    .map(x => x)\n    .filter(x => x);\n");
    }

    #[test]
    fn idempotent() {
        let src = "function f() {\n  if (a) {\n      b();\n  }\n}\n";
        let once = format(src, &Options::default());
        assert_eq!(format(&once, &Options::default()), once);
    }
}
