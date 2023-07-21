import { blazorCommentRegularExpression } from '../../Services/ComponentDescriptorDiscovery';

export interface INode {
  readonly nodeType: number;
  textContent: string | null;
}

export interface INodeRange extends Iterable<INode> {
  insertBefore(nodeToInsert: INode, beforeExistingNode: INode | null): void;
  remove(node: INode): void;
}

export interface CommentBoundedRange {
  startExclusive: Comment,
  endExclusive: Comment,
}

class NodeIterator implements Iterator<INode, null> {
  private nextVal: Node | null;
  constructor(parent: Node) {
    this.nextVal = parent.firstChild;
  }
  next(): IteratorResult<INode, null> {
    const result = this.nextVal;

    const componentMarker = result && parseComponentMarker(result);
    if (componentMarker) {
      this.nextVal = componentMarker.nextSibling;
    } else {
      this.nextVal = this.nextVal?.nextSibling || null;
    }

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

      const componentMarker = result && parseComponentMarker(result);
      if (componentMarker) {
        this.nextVal = componentMarker.nextSibling!;
      } else {
        this.nextVal = result.nextSibling!;
      }

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

function parseComponentMarker(node: Node): { nextSibling: Node | null } | null {
  if (node.nodeType === Node.COMMENT_NODE && node.textContent) {
    const definition = blazorCommentRegularExpression.exec(node.textContent!);
    const json = definition && definition.groups && definition.groups['descriptor'];
    if (json) {
      const parsedJson = JSON.parse(json) as { prerenderId?: string };
      if (parsedJson.prerenderId) {
        return { nextSibling: getComponentEndComment(node as Comment, parsedJson.prerenderId).nextSibling };
      } else {
        return { nextSibling: node.nextSibling };
      }
    }
  }

  return null;
}

function getComponentEndComment(startComment: Comment, prerenderId: string): Node {
  for (let candidateEndNode = startComment.nextSibling; candidateEndNode; candidateEndNode = candidateEndNode?.nextSibling) {
    if (candidateEndNode.nodeType === Node.COMMENT_NODE && candidateEndNode.textContent) {
      const definition = blazorCommentRegularExpression.exec(candidateEndNode.textContent!);
      const json = definition && definition[1];
      if (json) {
        const parsedJson = JSON.parse(json) as { prerenderId?: string };
        if (parsedJson.prerenderId === prerenderId) {
          return candidateEndNode;
        }
      }
    }
  }

  throw new Error(`No matching end comment for prerender ${prerenderId}`);
}
