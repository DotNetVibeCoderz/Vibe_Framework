//! Zero-copy JS.Net tokenizer. Tokens borrow slices of the source; no allocation per token.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Ident,
    Number,
    Str,
    Template,
    LineComment,
    BlockComment,
    Punct,
    Bad,
}

#[derive(Debug, Clone, Copy)]
pub struct Token<'a> {
    pub kind: Kind,
    pub text: &'a str,
    pub start: usize,
    pub line: usize,
    pub col: usize,
    /// True when the token is unterminated (string, template or block comment).
    pub unterminated: bool,
}

const PUNCT: [&str; 50] = [
    "...", "===", "!==", "**=", "<<=", "&&=", "||=", "??=", "=>", "==", "!=", "<=", ">=", "++", "--", "+=", "-=",
    "*=", "/=", "%=", "&=", "|=", "^=", "&&", "||", "??", "?.", "**", "<<", "{", "}", "(", ")", "[", "]", ";", ",",
    "<", ">", "+", "-", "*", "/", "%", "&", "|", "^", "!", "~", "?",
];
const PUNCT_TAIL: [&str; 4] = [":", "=", ".", "@"];

pub struct Lexer<'a> {
    src: &'a str,
    bytes: &'a [u8],
    pos: usize,
    line: usize,
    line_start: usize,
}

impl<'a> Lexer<'a> {
    pub fn new(src: &'a str) -> Self {
        Lexer { src, bytes: src.as_bytes(), pos: 0, line: 1, line_start: 0 }
    }

    fn peek(&self, n: usize) -> u8 {
        *self.bytes.get(self.pos + n).unwrap_or(&0)
    }

    fn bump(&mut self) {
        if self.bytes[self.pos] == b'\n' {
            self.line += 1;
            self.line_start = self.pos + 1;
        }
        self.pos += 1;
    }

    fn skip_ws(&mut self) {
        while self.pos < self.bytes.len() && (self.bytes[self.pos] as char).is_ascii_whitespace() {
            self.bump();
        }
    }

    fn is_ident_start(c: u8) -> bool {
        c.is_ascii_alphabetic() || c == b'_' || c == b'$' || c >= 0x80
    }

    fn is_ident_part(c: u8) -> bool {
        Self::is_ident_start(c) || c.is_ascii_digit()
    }

    fn skip_quoted(&mut self, quote: u8) -> bool {
        self.bump();
        while self.pos < self.bytes.len() {
            let c = self.bytes[self.pos];
            if c == b'\\' {
                self.bump();
                if self.pos < self.bytes.len() {
                    self.bump();
                }
                continue;
            }
            if c == b'\n' {
                return false;
            }
            self.bump();
            if c == quote {
                return true;
            }
        }
        false
    }

    /// Templates may nest: `a ${ `b ${c}` } d`.
    fn skip_template(&mut self) -> bool {
        self.bump(); // `
        let mut depth: Vec<usize> = Vec::new();
        while self.pos < self.bytes.len() {
            let c = self.bytes[self.pos];
            if depth.is_empty() {
                match c {
                    b'\\' => {
                        self.bump();
                        if self.pos < self.bytes.len() {
                            self.bump();
                        }
                    }
                    b'`' => {
                        self.bump();
                        return true;
                    }
                    b'$' if self.peek(1) == b'{' => {
                        self.bump();
                        self.bump();
                        depth.push(1);
                    }
                    _ => self.bump(),
                }
            } else {
                match c {
                    b'{' => {
                        *depth.last_mut().unwrap() += 1;
                        self.bump();
                    }
                    b'}' => {
                        let d = depth.last_mut().unwrap();
                        *d -= 1;
                        if *d == 0 {
                            depth.pop();
                        }
                        self.bump();
                    }
                    b'"' | b'\'' => {
                        self.skip_quoted(c);
                    }
                    b'`' => {
                        if !self.skip_template() {
                            return false;
                        }
                    }
                    _ => self.bump(),
                }
            }
        }
        false
    }

    pub fn next_token(&mut self) -> Option<Token<'a>> {
        self.skip_ws();
        if self.pos >= self.bytes.len() {
            return None;
        }
        let start = self.pos;
        let (line, col) = (self.line, self.pos - self.line_start + 1);
        let c = self.bytes[self.pos];
        let mut unterminated = false;
        let kind = if c == b'/' && self.peek(1) == b'/' {
            while self.pos < self.bytes.len() && self.bytes[self.pos] != b'\n' {
                self.bump();
            }
            Kind::LineComment
        } else if c == b'/' && self.peek(1) == b'*' {
            self.bump();
            self.bump();
            loop {
                if self.pos >= self.bytes.len() {
                    unterminated = true;
                    break;
                }
                if self.bytes[self.pos] == b'*' && self.peek(1) == b'/' {
                    self.bump();
                    self.bump();
                    break;
                }
                self.bump();
            }
            Kind::BlockComment
        } else if Self::is_ident_start(c) || (c == b'#' && Self::is_ident_start(self.peek(1))) {
            self.bump();
            while self.pos < self.bytes.len() && Self::is_ident_part(self.bytes[self.pos]) {
                self.bump();
            }
            Kind::Ident
        } else if c.is_ascii_digit() || (c == b'.' && self.peek(1).is_ascii_digit()) {
            while self.pos < self.bytes.len() {
                let d = self.bytes[self.pos];
                let exp_sign = (d == b'+' || d == b'-') && matches!(self.bytes[self.pos - 1], b'e' | b'E')
                    && !self.src[start..self.pos].starts_with("0x");
                if d.is_ascii_alphanumeric() || d == b'_' || d == b'.' || exp_sign {
                    self.bump();
                } else {
                    break;
                }
            }
            Kind::Number
        } else if c == b'"' || c == b'\'' {
            unterminated = !self.skip_quoted(c);
            Kind::Str
        } else if c == b'`' {
            unterminated = !self.skip_template();
            Kind::Template
        } else {
            let rest = &self.src[self.pos..];
            match PUNCT.iter().chain(PUNCT_TAIL.iter()).find(|p| rest.starts_with(**p)) {
                Some(p) => {
                    for _ in 0..p.len() {
                        self.bump();
                    }
                    Kind::Punct
                }
                None => {
                    // Advance one full UTF-8 character.
                    let ch_len = rest.chars().next().map(|ch| ch.len_utf8()).unwrap_or(1);
                    for _ in 0..ch_len {
                        self.bump();
                    }
                    Kind::Bad
                }
            }
        };
        Some(Token { kind, text: &self.src[start..self.pos], start, line, col, unterminated })
    }
}

pub fn tokenize(src: &str) -> Vec<Token<'_>> {
    let mut lx = Lexer::new(src);
    let mut out = Vec::with_capacity(src.len() / 4);
    while let Some(t) = lx.next_token() {
        out.push(t);
    }
    out
}
