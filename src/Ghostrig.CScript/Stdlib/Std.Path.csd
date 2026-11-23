/// VFS path manipulation (Document 7 §13.9), pure string work with the kernel's rules: paths are absolute, '/'
/// separated, '.' and '..' are resolved, and '..' can never escape the root. Invalid paths raise InvalidPath (EINVAL).
module Std.Path;

/// The canonical form of an absolute path.
string Normalize(string path);
bool IsValid(string path);
/// Joins and normalises; a later absolute part restarts the path.
string Join(string first, string second);
string Join(string[] parts);
/// Resolves a relative or absolute path against a directory.
string Resolve(string directory, string path);
/// The parent directory ("/" for "/").
string Parent(string path);
/// The last segment ("" for "/").
string Name(string path);
/// Lower-case extension with the dot (".csv"), or "".
string Extension(string path);
/// The name without its extension.
string Stem(string path);
string ChangeExtension(string path, string extension);
/// True when any segment starts with '.'.
bool IsHidden(string path);
string[] Segments(string path);
/// True when ancestor strictly contains path.
bool IsAncestor(string ancestor, string path);
/// The relative path from one directory to a path ("../logs/ftp.log").
string Relative(string from, string to);
/// Shell glob over whole paths: *, **, ?, [abc].
bool Glob(string pattern, string path);
