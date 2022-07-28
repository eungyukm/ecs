# Memory Allocator [![](Logo-Tiny.png)](/../../#glossary)

In ME.ECS there is custom Memory Allocator with custom pointers.
Memory Allocator stored in State and contains all objects you have create in state.

> **Warning**
> Keep in mind that MemoryAllocator doesn't thread-safe for now.

```csharp
// Get state allocator
var allocator = world.GetState().allocator;
// Create collection in state allocator
var arr = new List<int>(ref allocator, 100);
...
arr.Dispose(ref allocator);
```

The main difference between `MemoryAllocator.List<>` and `NativeList<>` (or `Generic.List<>`) is in pointer inside this collection:
* `Generic.List<>` contains array, which points to the C# heap memory.
* `NativeList<>` contains void*, which points to the Unity heap memory.
* `MemoryAllocator.List<>` contains long, which doesn't point to the native memory, it points to the allocator, so you can store this collection everywhere: in UI, in components, etc. You can store collections inside collections and doesn't matter about copying.

> **Warning**
> Do not forget to call `Dispose()` MemoryAllocator collections.

For now ME.ECS has these collections implemented:
| Collection | Description | Burst |
| ------ | ----- | ----- |
| ```MemArrayAllocator``` | Default array implementation. | :heavy_check_mark: |
| ```List``` | Default list implementation. | :heavy_check_mark: |
| ```HashSet``` | Default hash set implementation without IEquatable restriction (uses default `EqualityComparer`). |  |
| ```Dictionary``` | Default dictionary implementation without IEquatable restriction (uses default `EqualityComparer`). |  |
| ```Stack``` | Default stack implementation. |  |
| ```Queue``` | Default queue implementation. |  |
| ```EquatableHashSet``` | Custom HashSet implementation with default IEquatable restriction. Use this for primitive or types which has `IEquatable<>` interface. | :heavy_check_mark: |
| ```EquatableDictionary``` | Custom Dictionary implementation with default IEquatable restriction. Use this for primitive or types which has `IEquatable<>` interface. | :heavy_check_mark: |
| ```NativeHashSet``` | Custom HashSet implementation with `IEquatableAllocator<>` restriction. Required for types which needs to check Equals using MemoryAllocator. | :heavy_check_mark: |

```csharp
using ME.ECS.Collections.MemoryAllocator;

public struct YourComponent : IComponent {
    // You don't need to use ICopyable<> interface here because List<> is stored as unmanaged pointer
    public List<int> list;
}
```

## Static Allocators

By default ME.ECS has 2 static allocators:

```csharp
// Max size of temp allocator is 256 KB
var tempAllocator = StaticAllocators.GetAllocator(AllocatorType.Temp);
// Initial size of persistent allocator is 4MB and max size is not defined
var persistentAllocator = StaticAllocators.GetAllocator(AllocatorType.Persistent);
```

You can use any of these allocators to store your data. Btw, you can create your own allocator with options:

```csharp
var customAllocator = new MemoryAllocator().Initialize(initialSizeInBytes, maxSizeInBytes);
```

[![](Footer.png)](/../../#glossary)