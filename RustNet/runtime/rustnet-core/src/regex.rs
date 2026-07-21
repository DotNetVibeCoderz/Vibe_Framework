//! Compact backtracking regex engine for `System.Text.RegularExpressions`.
//!
//! Supported syntax: literals, `.`, `*`, `+`, `?`, `[...]`/`[^...]` with
//! ranges, `^`, `$`, escapes (`\d \D \w \W \s \S \.` etc.), groups `(...)`
//! and alternation `|`. Matching compiles to a small instruction VM and
//! runs a DFS with a visited set (no exponential blowup on empty loops).


#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
#[derive(Debug, Clone)]
enum Inst {
    Char(char),
    Any,
    Class { neg: bool, items: Vec<(char, char)> },
    Start,
    End,
    Split(usize, usize),
    Jmp(usize),
    Match,
}

pub struct Regex {
    prog: Vec<Inst>,
}

struct Parser<'a> {
    chars: Vec<char>,
    pos: usize,
    _pattern: &'a str,
}

impl<'a> Parser<'a> {
    fn peek(&self) -> Option<char> {
        self.chars.get(self.pos).copied()
    }
    fn next(&mut self) -> Option<char> {
        let c = self.peek();
        if c.is_some() {
            self.pos += 1;
        }
        c
    }
}

#[derive(Debug, Clone)]
enum Node {
    Char(char),
    Any,
    Class { neg: bool, items: Vec<(char, char)> },
    Start,
    End,
    Seq(Vec<Node>),
    Alt(Vec<Node>),
    Star(Box<Node>),
    Plus(Box<Node>),
    Opt(Box<Node>),
}

impl Regex {
    pub fn new(pattern: &str) -> Result<Regex, String> {
        let mut p = Parser { chars: pattern.chars().collect(), pos: 0, _pattern: pattern };
        let node = parse_alt(&mut p)?;
        if p.pos != p.chars.len() {
            return Err(format!("unexpected '{}' at {}", p.peek().unwrap_or('?'), p.pos));
        }
        let mut prog = Vec::new();
        compile(&node, &mut prog);
        prog.push(Inst::Match);
        Ok(Regex { prog })
    }

    /// Try to match starting exactly at `chars[at]`; returns match end.
    fn match_at(&self, chars: &[char], at: usize) -> Option<usize> {
        let mut stack = vec![(0usize, at)];
        let mut visited = alloc::collections::BTreeSet::new();
        let mut best: Option<usize> = None;
        while let Some((pc, pos)) = stack.pop() {
            if !visited.insert((pc, pos)) {
                continue;
            }
            match &self.prog[pc] {
                Inst::Match => {
                    best = Some(best.map_or(pos, |b: usize| b.max(pos)));
                }
                Inst::Char(c) => {
                    if chars.get(pos) == Some(c) {
                        stack.push((pc + 1, pos + 1));
                    }
                }
                Inst::Any => {
                    if pos < chars.len() {
                        stack.push((pc + 1, pos + 1));
                    }
                }
                Inst::Class { neg, items } => {
                    if let Some(&c) = chars.get(pos) {
                        let inside = items.iter().any(|&(lo, hi)| c >= lo && c <= hi);
                        if inside != *neg {
                            stack.push((pc + 1, pos + 1));
                        }
                    }
                }
                Inst::Start => {
                    if pos == 0 {
                        stack.push((pc + 1, pos));
                    }
                }
                Inst::End => {
                    if pos == chars.len() {
                        stack.push((pc + 1, pos));
                    }
                }
                Inst::Split(a, b) => {
                    // Push b first so a (greedy path) is explored first.
                    stack.push((*b, pos));
                    stack.push((*a, pos));
                }
                Inst::Jmp(a) => stack.push((*a, pos)),
            }
        }
        best
    }

    pub fn find(&self, text: &str, from: usize) -> Option<(usize, usize)> {
        let chars: Vec<char> = text.chars().collect();
        for start in from..=chars.len() {
            if let Some(end) = self.match_at(&chars, start) {
                return Some((start, end));
            }
        }
        None
    }

    pub fn is_match(&self, text: &str) -> bool {
        self.find(text, 0).is_some()
    }

    pub fn replace_all(&self, text: &str, replacement: &str) -> String {
        let chars: Vec<char> = text.chars().collect();
        let mut out = String::new();
        let mut pos = 0usize;
        while pos <= chars.len() {
            match self.match_at(&chars, pos) {
                Some(end) if end > pos => {
                    out.push_str(replacement);
                    pos = end;
                }
                Some(_) => {
                    // Empty match: emit replacement, advance one char.
                    out.push_str(replacement);
                    if pos < chars.len() {
                        out.push(chars[pos]);
                    }
                    pos += 1;
                }
                None => {
                    if pos < chars.len() {
                        out.push(chars[pos]);
                    }
                    pos += 1;
                }
            }
        }
        out
    }

    pub fn split(&self, text: &str) -> Vec<String> {
        let chars: Vec<char> = text.chars().collect();
        let mut parts = Vec::new();
        let mut piece = String::new();
        let mut pos = 0usize;
        while pos < chars.len() {
            match self.match_at(&chars, pos) {
                Some(end) if end > pos => {
                    parts.push(core::mem::take(&mut piece));
                    pos = end;
                }
                _ => {
                    piece.push(chars[pos]);
                    pos += 1;
                }
            }
        }
        parts.push(piece);
        parts
    }
}

fn parse_alt(p: &mut Parser) -> Result<Node, String> {
    let mut branches = vec![parse_seq(p)?];
    while p.peek() == Some('|') {
        p.next();
        branches.push(parse_seq(p)?);
    }
    Ok(if branches.len() == 1 { branches.pop().unwrap() } else { Node::Alt(branches) })
}

fn parse_seq(p: &mut Parser) -> Result<Node, String> {
    let mut items = Vec::new();
    while let Some(c) = p.peek() {
        if c == '|' || c == ')' {
            break;
        }
        items.push(parse_repeat(p)?);
    }
    Ok(Node::Seq(items))
}

fn parse_repeat(p: &mut Parser) -> Result<Node, String> {
    let atom = parse_atom(p)?;
    Ok(match p.peek() {
        Some('*') => {
            p.next();
            Node::Star(Box::new(atom))
        }
        Some('+') => {
            p.next();
            Node::Plus(Box::new(atom))
        }
        Some('?') => {
            p.next();
            Node::Opt(Box::new(atom))
        }
        _ => atom,
    })
}

fn parse_atom(p: &mut Parser) -> Result<Node, String> {
    match p.next() {
        None => Err("unexpected end of pattern".into()),
        Some('(') => {
            let inner = parse_alt(p)?;
            if p.next() != Some(')') {
                return Err("missing )".into());
            }
            Ok(inner)
        }
        Some('.') => Ok(Node::Any),
        Some('^') => Ok(Node::Start),
        Some('$') => Ok(Node::End),
        Some('[') => parse_class(p),
        Some('\\') => parse_escape(p),
        Some(c @ ('*' | '+' | '?' | ')')) => Err(format!("misplaced '{c}'")),
        Some(c) => Ok(Node::Char(c)),
    }
}

fn parse_escape(p: &mut Parser) -> Result<Node, String> {
    let c = p.next().ok_or("dangling escape")?;
    Ok(match c {
        'd' => Node::Class { neg: false, items: vec![('0', '9')] },
        'D' => Node::Class { neg: true, items: vec![('0', '9')] },
        'w' => Node::Class {
            neg: false,
            items: vec![('a', 'z'), ('A', 'Z'), ('0', '9'), ('_', '_')],
        },
        'W' => Node::Class {
            neg: true,
            items: vec![('a', 'z'), ('A', 'Z'), ('0', '9'), ('_', '_')],
        },
        's' => Node::Class {
            neg: false,
            items: vec![(' ', ' '), ('\t', '\t'), ('\n', '\n'), ('\r', '\r')],
        },
        'S' => Node::Class {
            neg: true,
            items: vec![(' ', ' '), ('\t', '\t'), ('\n', '\n'), ('\r', '\r')],
        },
        'n' => Node::Char('\n'),
        'r' => Node::Char('\r'),
        't' => Node::Char('\t'),
        other => Node::Char(other),
    })
}

fn parse_class(p: &mut Parser) -> Result<Node, String> {
    let mut neg = false;
    if p.peek() == Some('^') {
        p.next();
        neg = true;
    }
    let mut items = Vec::new();
    loop {
        let c = p.next().ok_or("unterminated [class]")?;
        if c == ']' {
            break;
        }
        let lo = if c == '\\' { p.next().ok_or("dangling escape in class")? } else { c };
        if p.peek() == Some('-') && p.chars.get(p.pos + 1) != Some(&']') {
            p.next();
            let hi = p.next().ok_or("bad range in class")?;
            items.push((lo, hi));
        } else {
            items.push((lo, lo));
        }
    }
    Ok(Node::Class { neg, items })
}

fn compile(node: &Node, prog: &mut Vec<Inst>) {
    match node {
        Node::Char(c) => prog.push(Inst::Char(*c)),
        Node::Any => prog.push(Inst::Any),
        Node::Class { neg, items } => prog.push(Inst::Class { neg: *neg, items: items.clone() }),
        Node::Start => prog.push(Inst::Start),
        Node::End => prog.push(Inst::End),
        Node::Seq(items) => {
            for n in items {
                compile(n, prog);
            }
        }
        Node::Alt(branches) => {
            // Chain of splits; each branch jumps to the common end.
            let mut jmp_slots = Vec::new();
            for (i, b) in branches.iter().enumerate() {
                if i + 1 < branches.len() {
                    let split_at = prog.len();
                    prog.push(Inst::Split(0, 0));
                    let body = prog.len();
                    compile(b, prog);
                    jmp_slots.push(prog.len());
                    prog.push(Inst::Jmp(0));
                    let next = prog.len();
                    prog[split_at] = Inst::Split(body, next);
                } else {
                    compile(b, prog);
                }
            }
            let end = prog.len();
            for slot in jmp_slots {
                prog[slot] = Inst::Jmp(end);
            }
        }
        Node::Star(inner) => {
            let split_at = prog.len();
            prog.push(Inst::Split(0, 0));
            let body = prog.len();
            compile(inner, prog);
            prog.push(Inst::Jmp(split_at));
            let end = prog.len();
            prog[split_at] = Inst::Split(body, end);
        }
        Node::Plus(inner) => {
            let body = prog.len();
            compile(inner, prog);
            let split_at = prog.len();
            prog.push(Inst::Split(body, split_at + 1));
        }
        Node::Opt(inner) => {
            let split_at = prog.len();
            prog.push(Inst::Split(0, 0));
            let body = prog.len();
            compile(inner, prog);
            let end = prog.len();
            prog[split_at] = Inst::Split(body, end);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn basics() {
        assert!(Regex::new("abc").unwrap().is_match("xxabcxx"));
        assert!(!Regex::new("^abc$").unwrap().is_match("xxabcxx"));
        assert!(Regex::new("^abc$").unwrap().is_match("abc"));
        assert!(Regex::new("a.c").unwrap().is_match("azc"));
        assert!(Regex::new("colou?r").unwrap().is_match("color"));
        assert!(Regex::new("colou?r").unwrap().is_match("colour"));
        assert!(Regex::new("ab+c").unwrap().is_match("abbbc"));
        assert!(!Regex::new("ab+c").unwrap().is_match("ac"));
        assert!(Regex::new("ab*c").unwrap().is_match("ac"));
    }

    #[test]
    fn classes_and_escapes() {
        let re = Regex::new(r"^\d\d:\d\d$").unwrap();
        assert!(re.is_match("12:34"));
        assert!(!re.is_match("1a:34"));
        assert!(Regex::new("[a-f0-9]+").unwrap().is_match("deadbeef"));
        assert!(Regex::new("[^0-9]+").unwrap().is_match("abc"));
        assert!(!Regex::new("^[^0-9]+$").unwrap().is_match("ab3c"));
        assert!(Regex::new(r"\w+@\w+\.\w+").unwrap().is_match("dev@rustnet.io"));
    }

    #[test]
    fn alternation_and_groups() {
        let re = Regex::new("^(cat|dog)s?$").unwrap();
        assert!(re.is_match("cat"));
        assert!(re.is_match("dogs"));
        assert!(!re.is_match("cow"));
        assert!(Regex::new("(ab)+").unwrap().is_match("ababab"));
    }

    #[test]
    fn replace_and_split() {
        let re = Regex::new(r"\d+").unwrap();
        assert_eq!(re.replace_all("a1b22c333", "#"), "a#b#c#");
        assert_eq!(re.split("a1b22c"), vec!["a", "b", "c"]);
        let ws = Regex::new(r"\s+").unwrap();
        assert_eq!(ws.split("hello   embedded world"), vec!["hello", "embedded", "world"]);
    }
}
