namespace Palladin.Core.Types.Exceptions;

public sealed class EntityNotFoundException(Type objectType, string objectKey) : Exception($"Entity of type {objectType.Name} with key {objectKey} not found.");
