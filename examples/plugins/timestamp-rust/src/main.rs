use serde::{Deserialize, Serialize};
use std::io::{self, BufRead, Write};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Deserialize)]
struct Request {
    req_id: i64,
    method: String,
    #[serde(default)]
    query: Option<String>,
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
    value: String,
    #[serde(skip_serializing_if = "is_false")]
    keep_open: bool,
}

fn is_false(value: &bool) -> bool {
    !*value
}

fn main() {
    let stdin = io::stdin();
    for line in stdin.lock().lines() {
        let Ok(line) = line else {
            break;
        };
        if line.trim().is_empty() {
            continue;
        }

        let response = match serde_json::from_str::<Request>(&line) {
            Ok(request) => handle(request),
            Err(error) => {
                eprintln!("invalid request: {error}");
                continue;
            }
        };

        if write_response(&response).is_err() {
            break;
        }
    }
}

fn handle(request: Request) -> Response {
    match request.method.as_str() {
        "query" => query(request.req_id, request.query.as_deref().unwrap_or("").trim()),
        "execute" => Response {
            req_id: request.req_id,
            results: None,
            ok: Some(true),
            error: None,
        },
        other => Response {
            req_id: request.req_id,
            results: None,
            ok: Some(false),
            error: Some(format!("unsupported method: {other}")),
        },
    }
}

fn query(req_id: i64, input: &str) -> Response {
    if input.is_empty() {
        return current_time_response(req_id);
    }

    let parsed = match input.parse::<i128>() {
        Ok(value) => value,
        Err(_) => {
            return Response {
                req_id,
                results: Some(Vec::new()),
                ok: None,
                error: Some("expected an integer Unix timestamp (seconds or milliseconds)".into()),
            };
        }
    };

    let is_millis = parsed.unsigned_abs() >= 100_000_000_000_u128;
    let total_ms = if is_millis {
        parsed
    } else {
        match parsed.checked_mul(1000) {
            Some(value) => value,
            None => {
                return Response {
                    req_id,
                    results: Some(Vec::new()),
                    ok: None,
                    error: Some("timestamp is out of range".into()),
                };
            }
        }
    };

    let Some(utc) = format_unix_utc(total_ms) else {
        return Response {
            req_id,
            results: Some(Vec::new()),
            ok: None,
            error: Some("timestamp is out of supported display range".into()),
        };
    };

    let seconds = total_ms.div_euclid(1000);
    Response {
        req_id,
        results: Some(vec![
            item("utc", &utc, "UTC date/time", &utc, false),
            item(
                "seconds",
                &seconds.to_string(),
                "Unix seconds",
                &seconds.to_string(),
                false,
            ),
            item(
                "milliseconds",
                &total_ms.to_string(),
                "Unix milliseconds",
                &total_ms.to_string(),
                false,
            ),
        ]),
        ok: None,
        error: None,
    }
}

fn current_time_response(req_id: i64) -> Response {
    let now = match SystemTime::now().duration_since(UNIX_EPOCH) {
        Ok(value) => value,
        Err(error) => {
            return Response {
                req_id,
                results: Some(Vec::new()),
                ok: None,
                error: Some(format!("system clock is before Unix epoch: {error}")),
            };
        }
    };

    let seconds = i128::from(now.as_secs());
    let milliseconds = seconds * 1000 + i128::from(now.subsec_millis());
    let utc = format_unix_utc(milliseconds).unwrap_or_else(|| "out of range".into());

    Response {
        req_id,
        results: Some(vec![
            item(
                "current-seconds",
                &seconds.to_string(),
                "Current Unix seconds",
                &seconds.to_string(),
                false,
            ),
            item(
                "current-milliseconds",
                &milliseconds.to_string(),
                "Current Unix milliseconds",
                &milliseconds.to_string(),
                false,
            ),
            item("current-utc", &utc, "Current UTC date/time", &utc, false),
        ]),
        ok: None,
        error: None,
    }
}

fn item(id: &str, title: &str, subtitle: &str, value: &str, keep_open: bool) -> ResultItem {
    ResultItem {
        id: id.into(),
        title: title.into(),
        subtitle: subtitle.into(),
        action: Action {
            r#type: "copy",
            value: value.into(),
            keep_open,
        },
    }
}

fn format_unix_utc(total_ms: i128) -> Option<String> {
    let seconds = total_ms.div_euclid(1000);
    let millis = total_ms.rem_euclid(1000);
    let days = seconds.div_euclid(86_400);
    let day_seconds = seconds.rem_euclid(86_400);

    let (year, month, day) = civil_from_days(days)?;
    let hour = day_seconds / 3600;
    let minute = (day_seconds % 3600) / 60;
    let second = day_seconds % 60;

    Some(format!(
        "{year:04}-{month:02}-{day:02} {hour:02}:{minute:02}:{second:02}.{millis:03} UTC"
    ))
}

fn civil_from_days(days_since_epoch: i128) -> Option<(i128, i128, i128)> {
    let z = days_since_epoch.checked_add(719_468)?;
    let era = if z >= 0 {
        z / 146_097
    } else {
        (z - 146_096) / 146_097
    };
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let mut year = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = mp + if mp < 10 { 3 } else { -9 };
    if month <= 2 {
        year += 1;
    }

    if !(-9999..=9999).contains(&year) {
        return None;
    }

    Some((year, month, day))
}

fn write_response(response: &Response) -> io::Result<()> {
    let stdout = io::stdout();
    let mut out = stdout.lock();
    serde_json::to_writer(&mut out, response).map_err(io::Error::other)?;
    out.write_all(b"\n")?;
    out.flush()
}
