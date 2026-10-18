use std::{
    path::{Component, Path, PathBuf},
    sync::Arc,
};

use crate::{config::WorkspaceConfig, error::CoreError};

/// 经过规范化的授权工作区。
///
/// 所有文件工具都必须通过本类型解析路径，避免 `..`、绝对路径和符号链接
/// 逃逸到授权目录之外。
#[derive(Debug)]
pub struct Workspace {
    root: PathBuf,
    allowed_roots: Vec<PathBuf>,
    config: WorkspaceConfig,
}

impl Workspace {
    /// 创建工作区并规范化所有根目录。
    pub fn new(config: WorkspaceConfig) -> Result<Arc<Self>, CoreError> {
        let root = canonical_dir(&config.root)?;
        let mut allowed_roots = Vec::new();
        if config.allowed_roots.is_empty() {
            allowed_roots.push(root.clone());
        } else {
            for candidate in &config.allowed_roots {
                allowed_roots.push(canonical_dir(candidate)?);
            }
            if !allowed_roots.iter().any(|allowed| allowed == &root) {
                return Err(CoreError::Config(format!(
                    "workspace root {} is not covered by workspace.allowed_roots",
                    root.display()
                )));
            }
        }
        Ok(Arc::new(Self {
            root,
            allowed_roots,
            config,
        }))
    }

    /// 会话主工作目录。
    pub fn root(&self) -> &Path {
        &self.root
    }

    /// 全部可读取根目录。
    pub fn allowed_roots(&self) -> &[PathBuf] {
        &self.allowed_roots
    }

    /// 当前工作区配置。
    pub fn config(&self) -> &WorkspaceConfig {
        &self.config
    }

    /// 解析一个必须已存在的路径用于读取。
    pub fn resolve_existing(&self, input: &str) -> Result<PathBuf, CoreError> {
        if input.trim().is_empty() {
            return Err(CoreError::Workspace("path must not be empty".into()));
        }
        let raw = self.absolute(input);
        let canonical = dunce::canonicalize(&raw).map_err(|err| {
            CoreError::Workspace(format!("cannot resolve {}: {err}", raw.display()))
        })?;
        if !self
            .allowed_roots
            .iter()
            .any(|root| canonical.starts_with(root))
        {
            return Err(CoreError::PermissionDenied(format!(
                "path {} is outside the authorized workspace roots",
                canonical.display()
            )));
        }
        self.reject_protected(&canonical)?;
        Ok(canonical)
    }

    /// 解析一个可能尚不存在的路径用于写入，返回规范化的目标路径。
    pub fn resolve_for_write(&self, input: &str) -> Result<PathBuf, CoreError> {
        if !self.config.writable {
            return Err(CoreError::PermissionDenied(
                "workspace is configured read-only".into(),
            ));
        }
        if input.trim().is_empty() {
            return Err(CoreError::Workspace("path must not be empty".into()));
        }
        let raw = self.absolute(input);

        if raw.exists() {
            let canonical = dunce::canonicalize(&raw).map_err(|err| {
                CoreError::Workspace(format!("cannot resolve {}: {err}", raw.display()))
            })?;
            if !canonical.starts_with(&self.root) {
                return Err(CoreError::PermissionDenied(format!(
                    "write target {} is outside the workspace",
                    canonical.display()
                )));
            }
            if canonical == self.root {
                return Err(CoreError::Workspace(
                    "cannot write to the workspace directory itself".into(),
                ));
            }
            self.reject_protected(&canonical)?;
            return Ok(canonical);
        }

        let file_name = raw
            .file_name()
            .and_then(|name| name.to_str())
            .ok_or_else(|| CoreError::Workspace("write target has no valid file name".into()))?;
        if file_name == "." || file_name == ".." {
            return Err(CoreError::Workspace("invalid write target".into()));
        }
        validate_file_name(file_name)?;

        let parent = raw
            .parent()
            .ok_or_else(|| CoreError::Workspace("write target has no parent directory".into()))?;
        let canonical_parent = dunce::canonicalize(parent).map_err(|err| {
            CoreError::Workspace(format!(
                "parent directory {} does not exist or cannot be resolved: {err}",
                parent.display()
            ))
        })?;
        if !canonical_parent.starts_with(&self.root) {
            return Err(CoreError::PermissionDenied(format!(
                "write target parent {} is outside the workspace",
                canonical_parent.display()
            )));
        }
        let target = canonical_parent.join(file_name);
        self.reject_protected(&target)?;
        Ok(target)
    }

    /// 解析写入目标，并在工作区内按需创建缺失的父目录。
    ///
    /// 创建目录前会先规范化最近的已存在祖先目录，确保所有新目录都落在
    /// 工作区内部。
    pub fn resolve_for_create(&self, input: &str) -> Result<PathBuf, CoreError> {
        if !self.config.writable {
            return Err(CoreError::PermissionDenied(
                "workspace is configured read-only".into(),
            ));
        }
        let raw = self.absolute(input);
        let parent = raw
            .parent()
            .ok_or_else(|| CoreError::Workspace("write target has no parent directory".into()))?;
        if !parent.exists() {
            let mut ancestor = parent;
            while !ancestor.exists() {
                ancestor = ancestor.parent().ok_or_else(|| {
                    CoreError::Workspace("write target has no existing ancestor".into())
                })?;
            }
            let canonical_ancestor = dunce::canonicalize(ancestor).map_err(|err| {
                CoreError::Workspace(format!(
                    "cannot resolve ancestor {}: {err}",
                    ancestor.display()
                ))
            })?;
            if !canonical_ancestor.starts_with(&self.root) {
                return Err(CoreError::PermissionDenied(format!(
                    "write target parent {} is outside the workspace",
                    parent.display()
                )));
            }
            let remainder = parent.strip_prefix(ancestor).map_err(|_| {
                CoreError::Workspace("cannot compute missing directory components".into())
            })?;
            for component in remainder.components() {
                if !matches!(component, Component::Normal(_)) {
                    return Err(CoreError::PermissionDenied(
                        "write target contains a non-normal path component".into(),
                    ));
                }
            }
            std::fs::create_dir_all(parent)?;
        }
        self.resolve_for_write(input)
    }

    /// 将绝对路径转换为工作区相对路径（统一使用 `/` 分隔符）。
    pub fn relative(&self, path: &Path) -> String {
        path.strip_prefix(&self.root)
            .unwrap_or(path)
            .components()
            .filter_map(|component| match component {
                Component::Normal(value) => Some(value.to_string_lossy().into_owned()),
                _ => None,
            })
            .collect::<Vec<_>>()
            .join("/")
    }

    /// 判断路径是否属于受保护的 `.git` 目录。
    pub fn is_git_internal(&self, path: &Path) -> bool {
        path.strip_prefix(&self.root)
            .map(|relative| {
                relative.components().any(|component| {
                    matches!(component, Component::Normal(value) if value.eq_ignore_ascii_case(".git"))
                })
            })
            .unwrap_or(false)
    }

    /// 将相对路径拼接到工作区根目录，绝对路径保持原样。
    fn absolute(&self, input: &str) -> PathBuf {
        let path = Path::new(input);
        if path.is_absolute() {
            path.to_path_buf()
        } else {
            self.root.join(path)
        }
    }

    /// 阻止对 `.git` 内部文件的访问。
    fn reject_protected(&self, path: &Path) -> Result<(), CoreError> {
        if self.config.protect_git_dir && self.is_git_internal(path) {
            return Err(CoreError::PermissionDenied(format!(
                "access to git metadata is blocked: {}",
                path.display()
            )));
        }
        Ok(())
    }
}

/// 将目录规范化为绝对路径，并确保它是目录。
fn canonical_dir(path: &Path) -> Result<PathBuf, CoreError> {
    let canonical = dunce::canonicalize(path).map_err(|err| {
        CoreError::Workspace(format!(
            "cannot resolve workspace root {}: {err}",
            path.display()
        ))
    })?;
    if !canonical.is_dir() {
        return Err(CoreError::Workspace(format!(
            "workspace root {} is not a directory",
            canonical.display()
        )));
    }
    Ok(canonical)
}

/// 拒绝 Windows 保留设备名和非法字符，避免写操作产生意外目标。
fn validate_file_name(name: &str) -> Result<(), CoreError> {
    if name.contains('\0') {
        return Err(CoreError::Workspace("file name contains NUL".into()));
    }
    if cfg!(windows) {
        if name.contains(':') {
            return Err(CoreError::Workspace(
                "file name contains ':' which is not allowed on Windows".into(),
            ));
        }
        let stem = name.split('.').next().unwrap_or(name).to_ascii_uppercase();
        let reserved = matches!(
            stem.as_str(),
            "CON"
                | "PRN"
                | "AUX"
                | "NUL"
                | "COM1"
                | "COM2"
                | "COM3"
                | "COM4"
                | "COM5"
                | "COM6"
                | "COM7"
                | "COM8"
                | "COM9"
                | "LPT1"
                | "LPT2"
                | "LPT3"
                | "LPT4"
                | "LPT5"
                | "LPT6"
                | "LPT7"
                | "LPT8"
                | "LPT9"
        );
        if reserved {
            return Err(CoreError::Workspace(format!(
                "file name {name} is reserved on Windows"
            )));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 创建临时工作区。
    fn fixture() -> (tempfile::TempDir, Arc<Workspace>) {
        let dir = tempfile::tempdir().unwrap();
        let workspace = Workspace::new(WorkspaceConfig {
            root: dir.path().to_path_buf(),
            ..WorkspaceConfig::default()
        })
        .unwrap();
        (dir, workspace)
    }

    #[test]
    fn resolves_relative_file() {
        let (dir, workspace) = fixture();
        std::fs::write(dir.path().join("a.txt"), "hello").unwrap();
        let path = workspace.resolve_existing("a.txt").unwrap();
        assert_eq!(workspace.relative(&path), "a.txt");
    }

    #[test]
    fn rejects_parent_escape() {
        let base = tempfile::tempdir().unwrap();
        let root = base.path().join("workspace");
        std::fs::create_dir(&root).unwrap();
        std::fs::write(base.path().join("a.txt"), "outside").unwrap();
        let workspace = Workspace::new(WorkspaceConfig {
            root,
            ..WorkspaceConfig::default()
        })
        .unwrap();
        let err = workspace.resolve_existing("../a.txt").unwrap_err();
        assert!(matches!(err, CoreError::PermissionDenied(_)));
    }

    #[test]
    fn rejects_absolute_outside_path() {
        let (_dir, workspace) = fixture();
        let outside = tempfile::tempdir().unwrap();
        let target = outside.path().join("secret.txt");
        std::fs::write(&target, "x").unwrap();
        let err = workspace
            .resolve_existing(target.to_str().unwrap())
            .unwrap_err();
        assert!(matches!(err, CoreError::PermissionDenied(_)));
    }

    #[test]
    fn write_parent_must_exist() {
        let (_dir, workspace) = fixture();
        let err = workspace
            .resolve_for_write("missing/child.txt")
            .unwrap_err();
        assert!(matches!(err, CoreError::Workspace(_)));
    }

    #[test]
    fn blocks_git_metadata_writes() {
        let (dir, workspace) = fixture();
        std::fs::create_dir_all(dir.path().join(".git")).unwrap();
        let err = workspace.resolve_for_write(".git/config").unwrap_err();
        assert!(matches!(err, CoreError::PermissionDenied(_)));
    }

    #[test]
    fn resolves_new_file_in_existing_directory() {
        let (dir, workspace) = fixture();
        std::fs::create_dir_all(dir.path().join("src")).unwrap();
        let path = workspace.resolve_for_write("src/new.rs").unwrap();
        assert!(path.ends_with("new.rs"));
        assert!(path.starts_with(workspace.root()));
    }
}
