//! jsnet-tools — native companion of the JS.Net compiler.
//!
//!   jsnet-tools fmt   <files|dirs> [--check] [--stdin] [--indent N]
//!   jsnet-tools check <files|dirs>        fast lexical/bracket validation (parallel)
//!   jsnet-tools tokens <file>             dump tokens
//!   jsnet-tools stats <files|dirs>        code statistics and lexer throughput
//!
//! Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

mod format;
mod lexer;

use lexer::{tokenize, Kind};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;
use std::time::Instant;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let code = match args.first().map(String::as_str) {
        Some("fmt") => cmd_fmt(&args[1..]),
        Some("check") => cmd_check(&args[1..]),
        Some("tokens") => cmd_tokens(&args[1..]),
        Some("stats") => cmd_stats(&args[1..]),
        Some("--version") | Some("-V") => {
            println!("jsnet-tools {}", env!("CARGO_PKG_VERSION"));
            0
        }
        _ => {
            print_help();
            if args.is_empty() { 0 } else { 2 }
        }
    };
    std::process::exit(code);
}

fn print_help() {
    println!("jsnet-tools {} — native tooling for JS.Net (Gravicode Studios)", env!("CARGO_PKG_VERSION"));
    println!();
    println!("USAGE:");
    println!("  jsnet-tools fmt <files|dirs> [--check] [--stdin] [--indent N]");
    println!("  jsnet-tools check <files|dirs>");
    println!("  jsnet-tools tokens <file>");
    println!("  jsnet-tools stats <files|dirs>");
}

/// Expands directories to `*.jsn` files (skipping bin/obj/node_modules/.git).
fn collect_files(inputs: &[String]) -> Vec<PathBuf> {
    fn walk(dir: &Path, out: &mut Vec<PathBuf>) {
        let Ok(entries) = std::fs::read_dir(dir) else { return };
        for e in entries.flatten() {
            let p = e.path();
            let name = p.file_name().and_then(|n| n.to_str()).unwrap_or("");
            if p.is_dir() {
                if !matches!(name, "bin" | "obj" | "node_modules" | ".git" | "target") {
                    walk(&p, out);
                }
            } else if p.extension().and_then(|x| x.to_str()) == Some("jsn") {
                out.push(p);
            }
        }
    }
    let mut files = Vec::new();
    for i in inputs.iter().filter(|a| !a.starts_with("--")) {
        let p = PathBuf::from(i);
        if p.is_dir() {
            walk(&p, &mut files);
        } else {
            files.push(p);
        }
    }
    files.sort();
    files
}

/// Runs `f` over files on all cores using scoped threads (no external crates).
fn par_for_each<F: Fn(&PathBuf) + Sync>(files: &[PathBuf], f: F) {
    let next = AtomicUsize::new(0);
    let threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4).min(files.len().max(1));
    std::thread::scope(|s| {
        for _ in 0..threads {
            s.spawn(|| loop {
                let i = next.fetch_add(1, Ordering::Relaxed);
                if i >= files.len() {
                    break;
                }
                f(&files[i]);
            });
        }
    });
}

fn cmd_fmt(args: &[String]) -> i32 {
    let check = args.iter().any(|a| a == "--check");
    let mut opts = format::Options::default();
    if let Some(i) = args.iter().position(|a| a == "--indent") {
        opts.indent = args.get(i + 1).and_then(|v| v.parse().ok()).unwrap_or(4);
    }
    if args.iter().any(|a| a == "--stdin") {
        let mut src = String::new();
        if std::io::stdin().read_to_string(&mut src).is_err() {
            return 2;
        }
        let out = format::format(&src, &opts);
        let _ = std::io::stdout().write_all(out.as_bytes());
        return 0;
    }
    let files = collect_files(args);
    let changed = Mutex::new(Vec::new());
    par_for_each(&files, |p| {
        let Ok(src) = std::fs::read_to_string(p) else { return };
        let out = format::format(&src, &opts);
        if out != src {
            if !check {
                let _ = std::fs::write(p, &out);
            }
            changed.lock().unwrap().push(p.display().to_string());
        }
    });
    let mut changed = changed.into_inner().unwrap();
    changed.sort();
    for c in &changed {
        println!("{} {}", if check { "would reformat" } else { "formatted" }, c);
    }
    println!("jsnet-tools fmt: {} file(s), {} {}", files.len(), changed.len(), if check { "need formatting" } else { "changed" });
    if check && !changed.is_empty() { 1 } else { 0 }
}

fn cmd_check(args: &[String]) -> i32 {
    let files = collect_files(args);
    let started = Instant::now();
    let errors = Mutex::new(Vec::<String>::new());
    par_for_each(&files, |p| {
        let Ok(src) = std::fs::read_to_string(p) else {
            errors.lock().unwrap().push(format!("{}: error JSNR000: cannot read file", p.display()));
            return;
        };
        let mut local = Vec::new();
        let mut stack: Vec<(char, usize, usize)> = Vec::new();
        for t in tokenize(&src) {
            if t.unterminated {
                let what = match t.kind {
                    Kind::Template => "template literal",
                    Kind::BlockComment => "block comment",
                    _ => "string literal",
                };
                local.push(format!("{}({},{}): error JSNR001: unterminated {}", p.display(), t.line, t.col, what));
            }
            if t.kind == Kind::Bad {
                local.push(format!("{}({},{}): error JSNR002: unexpected character '{}'", p.display(), t.line, t.col, t.text));
            }
            if t.kind == Kind::Punct {
                match t.text {
                    "{" | "(" | "[" => stack.push((t.text.chars().next().unwrap(), t.line, t.col)),
                    "}" | ")" | "]" => {
                        let close = t.text.chars().next().unwrap();
                        let open = match close { '}' => '{', ')' => '(', _ => '[' };
                        match stack.pop() {
                            Some((o, _, _)) if o == open => {}
                            Some((o, l, c)) => local.push(format!(
                                "{}({},{}): error JSNR003: '{}' does not match '{}' opened at {}:{}",
                                p.display(), t.line, t.col, close, o, l, c)),
                            None => local.push(format!("{}({},{}): error JSNR004: unmatched '{}'", p.display(), t.line, t.col, close)),
                        }
                    }
                    _ => {}
                }
            }
        }
        for (o, l, c) in stack {
            local.push(format!("{}({},{}): error JSNR005: '{}' is never closed", p.display(), l, c, o));
        }
        errors.lock().unwrap().extend(local);
    });
    let mut errors = errors.into_inner().unwrap();
    errors.sort();
    for e in &errors {
        eprintln!("{e}");
    }
    println!("jsnet-tools check: {} file(s), {} error(s) in {:.1} ms", files.len(), errors.len(), started.elapsed().as_secs_f64() * 1000.0);
    if errors.is_empty() { 0 } else { 1 }
}

fn cmd_tokens(args: &[String]) -> i32 {
    let Some(path) = args.first() else {
        eprintln!("usage: jsnet-tools tokens <file>");
        return 2;
    };
    let Ok(src) = std::fs::read_to_string(path) else {
        eprintln!("cannot read {path}");
        return 2;
    };
    for t in tokenize(&src) {
        println!("{:>4}:{:<3} @{:<6} {:<12} {}", t.line, t.col, t.start, format!("{:?}", t.kind), t.text.replace('\n', "\\n"));
    }
    0
}

fn cmd_stats(args: &[String]) -> i32 {
    let files = collect_files(args);
    let started = Instant::now();
    let totals = Mutex::new([0usize; 7]); // bytes, lines, blank, comment, tokens, classes, functions
    par_for_each(&files, |p| {
        let Ok(src) = std::fs::read_to_string(p) else { return };
        let toks = tokenize(&src);
        let mut comment_lines = std::collections::HashSet::new();
        let (mut classes, mut functions) = (0, 0);
        for t in toks.iter() {
            match t.kind {
                Kind::LineComment | Kind::BlockComment => {
                    for l in t.line..=t.line + t.text.matches('\n').count() {
                        comment_lines.insert(l);
                    }
                }
                Kind::Ident if t.text == "class" || t.text == "interface" || t.text == "struct" => classes += 1,
                Kind::Ident if t.text == "function" => functions += 1,
                Kind::Punct if t.text == "=>" => functions += 1,
                _ => {}
            }

        }
        let lines = src.lines().count();
        let blank = src.lines().filter(|l| l.trim().is_empty()).count();
        let mut tot = totals.lock().unwrap();
        tot[0] += src.len();
        tot[1] += lines;
        tot[2] += blank;
        tot[3] += comment_lines.len();
        tot[4] += toks.len();
        tot[5] += classes;
        tot[6] += functions;
    });
    let t = totals.into_inner().unwrap();
    let secs = started.elapsed().as_secs_f64().max(1e-9);
    println!("JS.Net code statistics");
    println!("  files      {:>10}", files.len());
    println!("  lines      {:>10}  (code {}, comments {}, blank {})", t[1], t[1].saturating_sub(t[2] + t[3]), t[3], t[2]);
    println!("  tokens     {:>10}", t[4]);
    println!("  types      {:>10}", t[5]);
    println!("  functions  {:>10}  (incl. arrow functions)", t[6]);
    println!("  throughput {:>10.1} MB/s  ({:.2} ms)", t[0] as f64 / 1_048_576.0 / secs, secs * 1000.0);
    0
}
