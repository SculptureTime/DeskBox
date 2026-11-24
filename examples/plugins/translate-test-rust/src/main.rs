use serde::{Deserialize, Serialize};
use std::io::{self, BufRead, Write};
use std::process;
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

#[derive(Debug, Deserialize)]
struct Request {
    req_id: i64,
    method: String,
    #[serde(default)]
    query: Option<String>,
    #[serde(default)]
    id: Option<String>,
}

#[derive(Serialize)]
struct Response {
    req_id: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    results: Option<Vec<ResultItem>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    ok: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

#[derive(Serialize)]
struct ResultItem {
    id: String,
    title: String,
    subtitle: String,
    action: Action,
}

#[derive(Serialize)]
struct Action {
    r#type: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    value: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    id: Option<String>,
    #[serde(skip_serializing_if = "is_false")]
    keep_open: bool,
}

fn is_false(value: &bool) -> bool {
    !*value
}

fn main() {
    let write_gate = Arc::new(Mutex::new(()));
    let stdin = io::stdin();

    for line in stdin.lock().lines() {
        let Ok(line) = line else {
            break;
        };
        if line.trim().is_empty() {
            continue;
        }

        let request = match serde_json::from_str::<Request>(&line) {
            Ok(request) => request,
            Err(error) => {
                eprintln!("invalid request: {error}");
                continue;
            }
        };

        let gate = Arc::clone(&write_gate);
        thread::spawn(move || {
            let response = handle(request);
            if let Some(response) = response {
                let _ = write_response(&gate, &response);
            }
        });
    }
}

fn handle(request: Request) -> Option<Response> {
    match request.method.as_str() {
        "query" => handle_query(request.req_id, request.query.as_deref().unwrap_or("")),
        "execute" => {
            let id = request.id.unwrap_or_default();
            eprintln!("execute callback received: {id}");
            Some(Response {
                req_id: request.req_id,
                results: None,
                ok: Some(true),
                error: None,
            })
        }
        other => Some(Response {
            req_id: request.req_id,
            results: None,
            ok: Some(false),
            error: Some(format!("unsupported method: {other}")),
        }),
    }
}

fn handle_query(req_id: i64, raw_query: &str) -> Option<Response> {
    let query = raw_query.trim();

    if query.eq_ignore_ascii_case("crash") {
        eprintln!("intentional crash requested");
        process::exit(42);
    }

    if query.eq_ignore_ascii_case("timeout") {
        thread::sleep(Duration::from_secs(10));
    }

    if query.eq_ignore_ascii_case("oversize") {
        let huge = "X".repeat(300 * 1024);
        return Some(copy_response(
            req_id,
            "oversize",
            huge,
            "Intentional >256 KiB stdout frame",
            false,
        ));
    }

    let (query, stderr_lines) = parse_usize_prefix(query, "stderr:");
    for index in 0..stderr_lines {
        eprintln!("stress stderr line {index:05}: {}", "x".repeat(120));
    }

    let (query, sleep_ms) = parse_u64_prefix(query, "sleep:");
    if sleep_ms > 0 {
        thread::sleep(Duration::from_millis(sleep_ms));
    }

    if query.eq_ignore_ascii_case("error") {
        return Some(Response {
            req_id,
            results: Some(Vec::new()),
            ok: None,
            error: Some("intentional plugin error".into()),
        });
    }

    if query.eq_ignore_ascii_case("plugin-action") {
        return Some(Response {
            req_id,
            results: Some(vec![ResultItem {
                id: "plugin-action".into(),
                title: "Execute plugin callback".into(),
                subtitle: "Host sends method=execute with id=ack".into(),
                action: Action {
                    r#type: "plugin",
                    value: None,
                    id: Some("ack".into()),
                    keep_open: false,
                },
            }]),
            ok: None,
            error: None,
        });
    }

    if query.eq_ignore_ascii_case("keep-open") {
        return Some(copy_response(
            req_id,
            "keep-open",
            "repeatable copy".into(),
            "keep_open=true; press Enter repeatedly",
            true,
        ));
    }

    let display = if query.is_empty() {
        "type text, sleep:800 text, timeout, stderr:1000 text, crash, oversize, plugin-action or keep-open"
            .to_string()
    } else {
        format!("TEST TRANSLATION: {query}")
    };

    Some(copy_response(
        req_id,
        "translation",
        display,
        "Transport stress harness — no external translation API",
        false,
    ))
}

fn copy_response(
    req_id: i64,
    id: &str,
    title: String,
    subtitle: &str,
    keep_open: bool,
) -> Response {
    Response {
        req_id,
        results: Some(vec![ResultItem {
            id: id.into(),
            action: Action {
                r#type: "copy",
                value: Some(title.clone()),
                id: None,
                keep_open,
            },
            title,
            subtitle: subtitle.into(),
        }]),
        ok: None,
        error: None,
    }
}

fn parse_u64_prefix<'a>(input: &'a str, prefix: &str) -> (&'a str, u64) {
    let Some(rest) = input.strip_prefix(prefix) else {
        return (input, 0);
    };
    let Some(index) = rest.find(char::is_whitespace) else {
        return ("", rest.parse::<u64>().unwrap_or(0));
    };
    let (number, tail) = rest.split_at(index);
    (tail.trim_start(), number.parse::<u64>().unwrap_or(0))
}

fn parse_usize_prefix<'a>(input: &'a str, prefix: &str) -> (&'a str, usize) {
    let (tail, value) = parse_u64_prefix(input, prefix);
    (tail, usize::try_from(value.min(100_000)).unwrap_or(100_000))
}

fn write_response(gate: &Mutex<()>, response: &Response) -> io::Result<()> {
    let _guard = gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    let stdout = io::stdout();
    let mut out = stdout.lock();
    serde_json::to_writer(&mut out, response).map_err(io::Error::other)?;
    out.write_all(b"\n")?;
    out.flush()
}
