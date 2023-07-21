// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { applyAnyDeferredValue } from '../DomSpecialPropertyUtil';
import { synchronizeAttributes } from './AttributeSync';
import { UpdateCost, ItemList, Operation, computeEditScript } from './EditScript';

interface INode {
  readonly nodeType: number;
  textContent: string | null;
}

interface INodeRange extends Iterable<INode> {
  insertBefore(nodeToInsert: INode, beforeExistingNode: INode | null): void;
  remove(node: INode): void;
}

class NodeIterator implements Iterator<INode, null> {
  private nextVal: Node | null;
  constructor(parent: Node) {
    this.nextVal = parent.firstChild;
  }
  next(): IteratorResult<INode, null> {
    const result = this.nextVal;
    this.nextVal = this.nextVal?.nextSibling || null;
    return result ? { value: result, done: false } : { value: null, done: true };
  }
}

class CommentBoundedRangeIterator implements Iterator<INode, null> {
  private nextVal: Node;
  private endMarker: Comment;
  constructor(range: CommentBoundedRange) {
    this.nextVal = range.startExclusive.nextSibling!;
    this.endMarker = range.endExclusive;
  }
  next(): IteratorResult<INode, null> {
    if (this.nextVal === this.endMarker) {
      return { value: null, done: true };
    } else {
      const result = this.nextVal!;
      this.nextVal = result.nextSibling!;
      return { value: result, done: false };
    }
  }
}

export function toINodeRange(container: CommentBoundedRange | Node): INodeRange {
  if (container instanceof Node) {
    return {
      [Symbol.iterator](): Iterator<INode, null> {
        return new NodeIterator(container);
      },
      remove(node) {
        container.removeChild(node as Node);
      },
      insertBefore(node, before) {
        container.insertBefore(node as Node, before as Node);
      }
    };
  } else {
    return {
      [Symbol.iterator](): Iterator<INode, null> {
        return new CommentBoundedRangeIterator(container);
      },
      remove(node) {
        container.startExclusive.parentNode!.removeChild(node as Node);
      },
      insertBefore(node, before) {
        container.startExclusive.parentNode!.insertBefore(node as Node, (before as Node || container.endExclusive));
      }
    }
  }
}

function toItemList(range: INodeRange): ItemList<INode> {
  const allNodes: INode[] = [];
  for (const x of range) {
    allNodes.push(x);
  }
  return new ArrayItemList(allNodes);
}

export function synchronizeDomContent(destination: INodeRange | CommentBoundedRange | Node, newContent: INodeRange | Node) {
  if (destination instanceof Node || (destination as CommentBoundedRange).startExclusive) {
    destination = toINodeRange(destination as CommentBoundedRange | Node);
  }
  if (newContent instanceof Node) {
    newContent = toINodeRange(newContent);
  }
  synchronizeDomContentCore(destination as INodeRange, newContent as INodeRange);
}

export function synchronizeDomContentCore(destination: INodeRange, newContent: INodeRange) {
  // Run the diff
  const editScript = computeEditScript(
    toItemList(destination),
    toItemList(newContent),
    domNodeComparer);

  // Handle any common leading items
  let destinationIterator = destination[Symbol.iterator]() as Iterator<INode, null>;
  let newContentIterator = newContent[Symbol.iterator]() as Iterator<INode, null>;
  let nextDestinationNode = destinationIterator.next().value;
  let nextNewContentNode = newContentIterator.next().value;
  for (let i = 0; i < editScript.skipCount; i++) {
    treatAsMatch(nextDestinationNode!, nextNewContentNode!);
    nextDestinationNode = destinationIterator.next().value;
    nextNewContentNode = newContentIterator.next().value;
  }

  // Handle any edited region
  if (editScript.edits) {
    const edits = editScript.edits;
    const editsLength = edits.length;

    for (let editIndex = 0; editIndex < editsLength; editIndex++) {
      const operation = edits[editIndex];
      switch (operation) {
        case Operation.Keep:
          treatAsMatch(nextDestinationNode!, nextNewContentNode!);
          nextDestinationNode = destinationIterator.next().value;
          nextNewContentNode = newContentIterator.next().value;
          break;
        case Operation.Update:
          treatAsSubstitution(nextDestinationNode!, nextNewContentNode!);
          nextDestinationNode = destinationIterator.next().value;
          nextNewContentNode = newContentIterator.next().value;
          break;
        case Operation.Delete:
          const nodeToRemove = nextDestinationNode!;
          nextDestinationNode = destinationIterator.next().value;
          destination.remove(nodeToRemove);
          break;
        case Operation.Insert:
          const nodeToInsert = nextNewContentNode!;
          nextNewContentNode = newContentIterator.next().value;
          destination.insertBefore(nodeToInsert, nextDestinationNode);
          break;
        default:
          throw new Error(`Unexpected operation: '${operation}'`);
      }
    }

    // Handle any common trailing items
    // These can only exist if there were some edits, otherwise everything would be in the set of common leading items
    while (nextDestinationNode) {
      treatAsMatch(nextDestinationNode!, nextNewContentNode!);
      nextDestinationNode = destinationIterator.next().value;
      nextNewContentNode = newContentIterator.next().value;
    }

    if (nextNewContentNode || nextDestinationNode) {
      // Should never be possible, as it would imply a bug in the edit script calculation, or possibly an unsupported
      // scenario like a DOM mutation observer modifying the destination nodes while we are working on them
      throw new Error('Updating the DOM failed because the sets of trailing nodes had inconsistent lengths.');
    }
  }
}

function treatAsMatch(destination: INode, source: INode) {
  switch (destination.nodeType) {
    case Node.TEXT_NODE:
    case Node.COMMENT_NODE:
      break;
    case Node.ELEMENT_NODE:
      const editableElementValue = getEditableElementValue(source as Element);
      synchronizeAttributes(destination as Element, source as Element);
      applyAnyDeferredValue(destination as Element);
      synchronizeDomContentCore(toINodeRange(destination as Element), toINodeRange(source as Element));

      // This is a much simpler alternative to the deferred-value-assignment logic we use in interactive rendering.
      // Because this sync algorithm goes depth-first, we know all the attributes and descendants are fully in sync
      // by now, so setting any "special value" property is just a matter of assigning it right now (we don't have
      // to be concerned that it's invalid because it doesn't correspond to an <option> child or a min/max attribute).
      if (editableElementValue !== null) {
        ensureEditableValueSynchronized(destination as Element, editableElementValue);
      }
      break;
    case Node.DOCUMENT_TYPE_NODE:
      // See comment below about doctype nodes. We leave them alone.
      break;
    default:
      throw new Error(`Not implemented: matching nodes of type ${destination.nodeType}`);
  }
}

function treatAsSubstitution(destination: INode, source: INode) {
  switch (destination.nodeType) {
    case Node.TEXT_NODE:
    case Node.COMMENT_NODE:
      (destination as Text).textContent = (source as Text).textContent;
      break;
    default:
      throw new Error(`Not implemented: substituting nodes of type ${destination.nodeType}`);
  }
}

function domNodeComparer(a: INode, b: INode): UpdateCost {
  if (a.nodeType !== b.nodeType) {
    return UpdateCost.Infinite;
  }

  switch (a.nodeType) {
    case Node.TEXT_NODE:
    case Node.COMMENT_NODE:
      // We're willing to update text and comment nodes in place, but treat the update operation as being
      // as costly as an insertion or deletion
      return a.textContent === b.textContent ? UpdateCost.None : UpdateCost.Some;
    case Node.ELEMENT_NODE:
      // For elements, we're only doing a shallow comparison and don't know if attributes/descendants are different.
      // We never 'update' one element type into another. We regard the update cost for same-type elements as zero because
      // then the 'find common prefix/suffix' optimization can include elements in those prefixes/suffixes.
      // TODO: If we want to support some way to force matching/nonmatching based on @key, we can add logic here
      //       to return UpdateCost.Infinite if either has a key but they don't match. This will prevent unwanted retention.
      //       For the converse (forcing retention, even if that means reordering), we could post-process the list of
      //       inserts/deletes to find matches based on key to treat those pairs as 'move' operations.
      return (a as Element).tagName === (b as Element).tagName ? UpdateCost.None : UpdateCost.Infinite;
    case Node.DOCUMENT_TYPE_NODE:
      // It's invalid to insert or delete doctype, and we have no use case for doing that. So just skip such
      // nodes by saying they are always unchanged.
      return UpdateCost.None;
    default:
      // For anything else we know nothing, so the risk-averse choice is to say we can't retain or update the old value
      return UpdateCost.Infinite;
  }
}

function ensureEditableValueSynchronized(destination: Element, value: any) {
  if (destination instanceof HTMLTextAreaElement && destination.value !== value) {
    destination.value = value as string;
  } else if (destination instanceof HTMLSelectElement && destination.selectedIndex !== value) {
    destination.selectedIndex = value as number;
  } else if (destination instanceof HTMLInputElement) {
    if (destination.type === 'checkbox' && destination.checked !== value) {
      destination.checked = value as boolean;
    } else if (destination.value !== value) {
      destination.value = value as string;
    }
  }
}

function getEditableElementValue(elem: Element): string | boolean | number | null {
  if (elem instanceof HTMLSelectElement) {
    return elem.selectedIndex;
  } else if (elem instanceof HTMLInputElement) {
    return elem.type === 'checkbox' ? elem.checked : (elem.getAttribute('value') || '');
  } else if (elem instanceof HTMLTextAreaElement) {
    return elem.value;
  } else {
    return null;
  }
}

export interface CommentBoundedRange {
  startExclusive: Comment,
  endExclusive: Comment,
}

// TODO: Instead of pre-evaluating the array, consider changing EditScript not to rely on random access to arbitrary indices
class ArrayItemList<T> implements ItemList<T> {
  constructor(private items: T[]) {
    this.length = items.length;
  }
  readonly length: number;
  item(index: number): T | null {
    return this.items[index];
  }
  forEach(callbackfn: (value: T, key: number, parent: ItemList<T>) => void, thisArg?: any): void {
    for (let i = 0; i < this.length; i++) {
      callbackfn.call(thisArg, this.items[i]!, i, this);
    }
  }
}
