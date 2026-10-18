use std::fs;

use async_trait::async_trait;
use globset::{Glob, GlobSet, GlobSetBuilder};
use ignore::WalkBuilder;
use regex::RegexBuilder;
use serde_json::{Value, json};

use super::{
    Tool, ToolContext, ToolError, ToolOutput, optional_bool, optional_str, optional_u64,
    required_str,
};
use crate::model::ToolSpec;

/// 代码库搜索工具，支持字面量、正则与符号定义检索。
pub struct SearchTool;

#[async_trait]
impl Tool for SearchTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "search".into(),
            description: "Search the workspace. mode=literal matches plain text, mode=regex uses a Rust regex, mode=symbol finds function/class/type definitions (query optionally filters symbol names). Respects .gitignore and returns path:line matches.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "query": {"type": "string", "description": "Text, regex, or optional symbol-name filter."},
                    "mode": {"type": "string", "enum": ["literal", "regex", "symbol"], "default": "literal"},
                    "case_sensitive": {"type": "boolean", "default": false},
                    "globs": {"type": "array", "items": {"type": "string"}, "description": "Optional path globs such as [\"**/*.rs\"]."},
                    "max_results": {"type": "integer", "minimum": 1, "maximum": 2000},
                    "include_hidden": {"type": "boolean", "default": false}
                },
                "required": ["query"],
                "additionalProperties": false
            }),
        }
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let query = required_str(&args, "query")?;
        let mode = optional_str(&args, "mode").unwrap_or_else(|| "literal".into());
        let case_sensitive = optional_bool(&args, "case_sensitive", false);
        let max_results = optional_u64(&args, "max_results")
            .unwrap_or(100)
            .clamp(1, 2_000) as usize;
        let include_hidden = optional_bool(&args, "include_hidden", false);
        let globs = build_globs(&args)?;

        let pattern = match mode.as_str() {
            "literal" => {
                if query.is_empty() {
                    return Err(ToolError::InvalidArguments(
                        "literal search query must not be empty".into(),
                    ));
                }
                regex::escape(&query)
            }
            "regex" => {
                if query.is_empty() {
                    return Err(ToolError::InvalidArguments(
                        "regex search query must not be empty".into(),
                    ));
                }
                query.clone()
            }
            "symbol" => symbol_pattern(),
            other => {
                return Err(ToolError::InvalidArguments(format!(
                    "unknown search mode `{other}`; expected literal, regex or symbol"
                )));
            }
        };
        let matcher = RegexBuilder::new(&pattern)
            .case_insensitive(!case_sensitive)
            .build()
            .map_err(|err| ToolError::InvalidArguments(format!("invalid regex: {err}")))?;
        let symbol_filter = (mode == "symbol" && !query.is_empty()).then(|| {
            RegexBuilder::new(&regex::escape(&query))
                .case_insensitive(!case_sensitive)
                .build()
                .expect("escaped literal regex must compile")
        });

        let walker = WalkBuilder::new(ctx.workspace.root())
            .hidden(!include_hidden)
            .git_ignore(true)
            .git_global(true)
            .git_exclude(true)
            .ignore(true)
            .parents(true)
            .filter_entry(|entry| {
                !entry
                    .file_name()
                    .to_str()
                    .map(|name| name.eq_ignore_ascii_case(".git"))
                    .unwrap_or(false)
            })
            .build();

        let mut matches = Vec::new();
        let mut scanned_files = 0u64;
        let mut limit_reached = false;
        for entry in walker {
            if ctx.cancel.is_cancelled() {
                return Err(ToolError::Canceled);
            }
            let entry = match entry {
                Ok(entry) => entry,
                Err(_) => continue,
            };
            if !entry
                .file_type()
                .map(|kind| kind.is_file())
                .unwrap_or(false)
            {
                continue;
            }
            let path = entry.path();
            if ctx.workspace.is_git_internal(path) {
                continue;
            }
            let relative = ctx.workspace.relative(path);
            if let Some(globs) = &globs
                && !globs.is_match(&relative)
            {
                continue;
            }
            let metadata = match entry.metadata() {
                Ok(metadata) => metadata,
                Err(_) => continue,
            };
            if metadata.len() > 2_097_152 {
                continue;
            }
            let bytes = match fs::read(path) {
                Ok(bytes) => bytes,
                Err(_) => continue,
            };
            if bytes.iter().take(4_096).any(|byte| *byte == 0) {
                continue;
            }
            let text = match String::from_utf8(bytes) {
                Ok(text) => text,
                Err(_) => continue,
            };
            scanned_files += 1;
            for (index, line) in text.lines().enumerate() {
                if !matcher.is_match(line) {
                    continue;
                }
                if let Some(filter) = &symbol_filter
                    && !filter.is_match(line)
                {
                    continue;
                }
                matches.push(format!("{relative}:{}: {}", index + 1, line.trim_end()));
                if matches.len() >= max_results {
                    limit_reached = true;
                    break;
                }
            }
            if limit_reached {
                break;
            }
        }

        let content = if matches.is_empty() {
            format!("no matches for `{query}` in {scanned_files} scanned files")
        } else {
            let mut content = matches.join("\n");
            if limit_reached {
                content.push_str(&format!("\n[result limit reached: {max_results}]"));
            }
            content
        };
        Ok(ToolOutput::text(
            ctx.clamp_output(content),
            format!(
                "searched {scanned_files} files, {} matches",
                matches.len().min(max_results)
            ),
        ))
    }
}

/// 构建可选的路径过滤 glob 集合；未提供时返回 `None`。
fn build_globs(args: &Value) -> Result<Option<GlobSet>, ToolError> {
    let Some(values) = args.get("globs").and_then(Value::as_array) else {
        return Ok(None);
    };
    if values.is_empty() {
        return Ok(None);
    }
    let mut builder = GlobSetBuilder::new();
    for value in values {
        let pattern = value
            .as_str()
            .ok_or_else(|| ToolError::InvalidArguments("globs must contain only strings".into()))?;
        builder.add(
            Glob::new(pattern)
                .map_err(|err| ToolError::InvalidArguments(format!("invalid glob: {err}")))?,
        );
    }
    builder
        .build()
        .map(Some)
        .map_err(|err| ToolError::InvalidArguments(format!("cannot build glob set: {err}")))
}

/// 跨语言的常见符号定义模式。
fn symbol_pattern() -> String {
    concat!(
        r"(?m)^\s*",
        r"(?:#\[[^\]]+\]\s*)?",
        r"(?:pub(?:\([^)]*\))?\s+)?",
        r"(?:export\s+)?",
        r"(?:(?:public|private|protected|internal|static|final|abstract)\s+)*",
        r"(?:async\s+)?(?:unsafe\s+)?(?:extern\s+)?",
        r"(?:fn|struct|enum|trait|impl|mod|type|const|static|function|class|interface|record)\s+",
        r"[A-Za-z_][A-Za-z0-9_]*",
        r"|^\s*(?:pub\s+)?async\s+fn\s+[A-Za-z_][A-Za-z0-9_]*",
        r"|^\s*def\s+[A-Za-z_][A-Za-z0-9_]*",
        r"|^\s*func\s+(?:\([^)]*\)\s+)?[A-Za-z_][A-Za-z0-9_]*"
    )
    .to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn symbol_pattern_matches_common_definitions() {
        let regex = regex::Regex::new(&symbol_pattern()).unwrap();
        assert!(regex.is_match("pub async fn run_agent() {}"));
        assert!(regex.is_match("class TaskRunner:"));
        assert!(regex.is_match("def execute(self):"));
        assert!(regex.is_match("func (s *Server) Start() {}"));
        assert!(regex.is_match("  public class Widget {"));
    }
}
