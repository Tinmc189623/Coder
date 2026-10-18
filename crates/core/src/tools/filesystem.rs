use std::{
    fs,
    io::{Read, Write},
    path::Path,
};

use async_trait::async_trait;
use coder_protocol::{FileChangeKind, RiskLevel};
use globset::{Glob, GlobSetBuilder};
use ignore::WalkBuilder;
use serde_json::{Value, json};
use similar::TextDiff;
use tempfile::NamedTempFile;

use super::{
    FileChange, Tool, ToolContext, ToolError, ToolOutput, emit_file_change, optional_bool,
    optional_str, optional_u64, required_str,
};
use crate::model::ToolSpec;

/// 读取工作区文本文件。
pub struct ReadFileTool;

#[async_trait]
impl Tool for ReadFileTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "read_file".into(),
            description: "Read a UTF-8 text file inside the authorized workspace. Returns real file contents; refuses binary files and files over the configured size limit.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "Path relative to the workspace root (absolute paths inside the workspace are also accepted)."},
                    "offset": {"type": "integer", "minimum": 1, "description": "1-based starting line."},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 5000, "description": "Maximum number of lines to return."}
                },
                "required": ["path"],
                "additionalProperties": false
            }),
        }
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let path = ctx
            .workspace
            .resolve_existing(&required_str(&args, "path")?)?;
        let metadata = fs::metadata(&path)?;
        if !metadata.is_file() {
            return Err(ToolError::InvalidArguments(format!(
                "{} is not a regular file",
                ctx.workspace.relative(&path)
            )));
        }
        if metadata.len() as usize > ctx.workspace.config().max_read_bytes {
            return Err(ToolError::InvalidArguments(format!(
                "file is {} bytes, exceeding max_read_bytes={}",
                metadata.len(),
                ctx.workspace.config().max_read_bytes
            )));
        }
        let content = read_text(&path)?;
        let display = ctx.workspace.relative(&path);

        let offset = optional_u64(&args, "offset").unwrap_or(1);
        let limit = optional_u64(&args, "limit");
        let (body, summary) = if offset > 1 || limit.is_some() {
            if offset == 0 {
                return Err(ToolError::InvalidArguments(
                    "offset is 1-based and must be >= 1".into(),
                ));
            }
            let total = content.lines().count();
            let limit = limit.unwrap_or(5000).clamp(1, 5000) as usize;
            let start = (offset - 1) as usize;
            let selected: Vec<&str> = content.lines().skip(start).take(limit).collect();
            let end = start + selected.len();
            let body = selected.join("\n");
            (
                format!(
                    "[lines {}-{} of {total}]\n{body}",
                    start + 1,
                    end.max(start + 1)
                ),
                format!("{display} lines {}-{}", start + 1, end.max(start + 1)),
            )
        } else {
            (
                content.clone(),
                format!("{display} ({} bytes)", content.len()),
            )
        };
        Ok(ToolOutput::text(ctx.clamp_output(body), summary))
    }
}

/// 创建或覆盖工作区文本文件。
pub struct WriteFileTool;

#[async_trait]
impl Tool for WriteFileTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "write_file".into(),
            description: "Create or completely overwrite a UTF-8 text file inside the workspace. Missing parent directories are created inside the workspace. Performs an atomic write.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "path": {"type": "string"},
                    "content": {"type": "string"}
                },
                "required": ["path", "content"],
                "additionalProperties": false
            }),
        }
    }

    fn risk(&self, _args: &Value) -> RiskLevel {
        RiskLevel::Medium
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let path = ctx
            .workspace
            .resolve_for_create(&required_str(&args, "path")?)?;
        let content = required_str(&args, "content")?;
        if content.len() > ctx.workspace.config().max_write_bytes {
            return Err(ToolError::InvalidArguments(format!(
                "content is {} bytes, exceeding max_write_bytes={}",
                content.len(),
                ctx.workspace.config().max_write_bytes
            )));
        }
        let existed = path.exists();
        atomic_write(&path, content.as_bytes())?;
        let kind = if existed {
            FileChangeKind::Modified
        } else {
            FileChangeKind::Created
        };
        let display = ctx.workspace.relative(&path);
        emit_file_change(ctx, &path, kind);
        Ok(ToolOutput {
            content: format!(
                "{} {display} ({} bytes)",
                if existed { "updated" } else { "created" },
                content.len()
            ),
            summary: format!("{} {display}", if existed { "updated" } else { "created" }),
            changed_files: vec![FileChange {
                path: display,
                kind,
            }],
        })
    }
}

/// 通过精确字符串替换编辑文件。
pub struct EditFileTool;

#[async_trait]
impl Tool for EditFileTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "edit_file".into(),
            description: "Replace an exact string in an existing UTF-8 file. By default old_string must occur exactly once; set replace_all=true to replace every occurrence. Returns a unified diff of the real change.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "path": {"type": "string"},
                    "old_string": {"type": "string"},
                    "new_string": {"type": "string"},
                    "replace_all": {"type": "boolean", "default": false}
                },
                "required": ["path", "old_string", "new_string"],
                "additionalProperties": false
            }),
        }
    }

    fn risk(&self, _args: &Value) -> RiskLevel {
        RiskLevel::Medium
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let path = ctx
            .workspace
            .resolve_for_write(&required_str(&args, "path")?)?;
        let old_string = required_str(&args, "old_string")?;
        let new_string = required_str(&args, "new_string")?;
        if old_string.is_empty() {
            return Err(ToolError::InvalidArguments(
                "old_string must not be empty; use write_file for new files".into(),
            ));
        }
        if !path.is_file() {
            return Err(ToolError::NotFound(format!(
                "{} is not an existing file",
                ctx.workspace.relative(&path)
            )));
        }
        let before = read_text(&path)?;
        let count = before.matches(&old_string).count();
        if count == 0 {
            return Err(ToolError::Execution(format!(
                "old_string was not found in {}",
                ctx.workspace.relative(&path)
            )));
        }
        let replace_all = optional_bool(&args, "replace_all", false);
        if count > 1 && !replace_all {
            return Err(ToolError::Execution(format!(
                "old_string occurs {count} times; provide more context or set replace_all=true"
            )));
        }
        let after = if replace_all {
            before.replace(&old_string, &new_string)
        } else {
            before.replacen(&old_string, &new_string, 1)
        };
        if after.len() > ctx.workspace.config().max_write_bytes {
            return Err(ToolError::InvalidArguments(format!(
                "edited file would exceed max_write_bytes={}",
                ctx.workspace.config().max_write_bytes
            )));
        }
        atomic_write(&path, after.as_bytes())?;
        let display = ctx.workspace.relative(&path);
        emit_file_change(ctx, &path, FileChangeKind::Modified);
        let diff = TextDiff::from_lines(&before, &after)
            .unified_diff()
            .context_radius(3)
            .header(&format!("a/{display}"), &format!("b/{display}"))
            .to_string();
        let diff = clamp_chars(diff, 12_000);
        Ok(ToolOutput {
            content: format!("edited {display} ({count} match(es) replaced)\n\n{diff}"),
            summary: format!("edited {display}"),
            changed_files: vec![FileChange {
                path: display,
                kind: FileChangeKind::Modified,
            }],
        })
    }
}

/// 列出工作区文件。
pub struct ListFilesTool;

#[async_trait]
impl Tool for ListFilesTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "list_files".into(),
            description: "List files and directories under the workspace, respecting .gitignore. Supports an optional glob filter such as \"**/*.rs\" and a depth limit.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "Base directory relative to the workspace; defaults to the root."},
                    "glob": {"type": "string", "description": "Glob matched against workspace-relative paths."},
                    "max_depth": {"type": "integer", "minimum": 1, "maximum": 64},
                    "max_results": {"type": "integer", "minimum": 1, "maximum": 5000},
                    "include_hidden": {"type": "boolean", "default": false}
                },
                "additionalProperties": false
            }),
        }
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let base = if let Some(path) = optional_str(&args, "path") {
            ctx.workspace.resolve_existing(&path)?
        } else {
            ctx.workspace.root().to_path_buf()
        };
        if !base.is_dir() {
            return Err(ToolError::InvalidArguments(format!(
                "{} is not a directory",
                ctx.workspace.relative(&base)
            )));
        }
        let max_depth = optional_u64(&args, "max_depth").unwrap_or(8).clamp(1, 64) as usize;
        let max_results = optional_u64(&args, "max_results")
            .unwrap_or(500)
            .clamp(1, 5000) as usize;
        let include_hidden = optional_bool(&args, "include_hidden", false);

        let mut builder = GlobSetBuilder::new();
        let has_glob = if let Some(glob) = optional_str(&args, "glob") {
            builder.add(
                Glob::new(&glob)
                    .map_err(|err| ToolError::InvalidArguments(format!("invalid glob: {err}")))?,
            );
            true
        } else {
            false
        };
        let globset = builder
            .build()
            .map_err(|err| ToolError::InvalidArguments(format!("cannot build glob set: {err}")))?;

        let mut entries = Vec::new();
        let walker = WalkBuilder::new(&base)
            .hidden(!include_hidden)
            .git_ignore(true)
            .git_global(true)
            .git_exclude(true)
            .ignore(true)
            .parents(true)
            .max_depth(Some(max_depth))
            .filter_entry(|entry| {
                !entry
                    .file_name()
                    .to_str()
                    .map(|name| name.eq_ignore_ascii_case(".git"))
                    .unwrap_or(false)
            })
            .build();

        for entry in walker {
            if ctx.cancel.is_cancelled() {
                return Err(ToolError::Canceled);
            }
            let entry = match entry {
                Ok(entry) => entry,
                Err(_) => continue,
            };
            if entry.depth() == 0 {
                continue;
            }
            let path = entry.path();
            if ctx.workspace.is_git_internal(path) {
                continue;
            }
            let relative = ctx.workspace.relative(path);
            if relative.is_empty() {
                continue;
            }
            if has_glob && !globset.is_match(&relative) {
                continue;
            }
            let mut display = relative;
            if entry.file_type().map(|kind| kind.is_dir()).unwrap_or(false) {
                display.push('/');
            }
            entries.push(display);
            if entries.len() >= max_results {
                break;
            }
        }
        entries.sort();
        let truncated = entries.len() >= max_results;
        let content = if entries.is_empty() {
            "(no matching files)".to_owned()
        } else {
            entries.join("\n")
        };
        let content = if truncated {
            format!("{content}\n[result limit reached: {max_results}]")
        } else {
            content
        };
        Ok(ToolOutput::text(
            ctx.clamp_output(content),
            format!("listed {} entries", entries.len()),
        ))
    }
}

/// 读取 UTF-8 文本并拒绝二进制文件。
fn read_text(path: &Path) -> Result<String, ToolError> {
    let mut file = fs::File::open(path)?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    if bytes.iter().take(8_192).any(|byte| *byte == 0) {
        return Err(ToolError::InvalidArguments(format!(
            "{} appears to be a binary file",
            path.display()
        )));
    }
    String::from_utf8(bytes)
        .map_err(|_| ToolError::InvalidArguments(format!("{} is not valid UTF-8", path.display())))
}

/// 原子写入文件；Windows 上必要时先移除旧目标再替换。
fn atomic_write(path: &Path, bytes: &[u8]) -> Result<(), ToolError> {
    let parent = path
        .parent()
        .ok_or_else(|| ToolError::Io("target has no parent directory".into()))?;
    let mut temp = NamedTempFile::new_in(parent)?;
    if let Ok(metadata) = fs::metadata(path) {
        let _ = temp.as_file().set_permissions(metadata.permissions());
    }
    temp.write_all(bytes)?;
    temp.as_file().sync_all()?;
    match temp.persist(path) {
        Ok(_) => Ok(()),
        Err(error) if cfg!(windows) && path.exists() => {
            fs::remove_file(path)?;
            error
                .file
                .persist(path)
                .map(|_| ())
                .map_err(|err| ToolError::Io(err.error.to_string()))
        }
        Err(error) => Err(ToolError::Io(error.error.to_string())),
    }
}

/// 按字符数截断文本。
fn clamp_chars(text: String, max: usize) -> String {
    if text.chars().count() <= max {
        text
    } else {
        let value: String = text.chars().take(max).collect();
        format!("{value}\n[diff truncated]")
    }
}
