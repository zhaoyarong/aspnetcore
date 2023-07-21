import { ComponentDescriptor, ServerComponentDescriptor, WebAssemblyComponentDescriptor, blazorCommentRegularExpression, discoverComponents } from '../Services/ComponentDescriptorDiscovery';
import { synchronizeDomContent, INode, INodeRange } from './DomMerging/DomSync';
import { LogicalElement, getLogicalRootDescriptor, toLogicalRootCommentElement } from './LogicalElements';

let descriptorHandler: (descriptor: ComponentDescriptor) => void | undefined;

export function attachComponentDescriptorHandler(handler: (descriptor: ComponentDescriptor) => void) {
  descriptorHandler = handler;
}

export function registerAllComponentDescriptors(root: Node) {
  const descriptors = upgradeComponentCommentsToLogicalRootComments(root);

  for (const descriptor of descriptors) {
    descriptorHandler(descriptor);
  }
}

export function synchronizeDomContentAndUpdateInteractiveComponents(destination: CommentBoundedRange | Node, newContent: Node) {
  synchronizeDomContent(toINodeRange(destination), toINodeRange(newContent));
}

export interface CommentBoundedRange {
  startExclusive: Comment,
  endExclusive: Comment,
}

function upgradeComponentCommentsToLogicalRootComments(root: Node): ComponentDescriptor[] {
  const serverDescriptors = discoverComponents(root, 'server') as ServerComponentDescriptor[];
  const webAssemblyDescriptors = discoverComponents(root, 'webassembly') as WebAssemblyComponentDescriptor[];
  const allDescriptors: ComponentDescriptor[] = [];

  for (const descriptor of [...serverDescriptors, ...webAssemblyDescriptors]) {
    const existingDescriptor = getLogicalRootDescriptor(descriptor.start as unknown as LogicalElement);
    if (existingDescriptor) {
      allDescriptors.push(existingDescriptor);
    } else {
      toLogicalRootCommentElement(descriptor);

      // Since we've already parsed the payloads from the start and end comments,
      // we sanitize them to reduce noise in the DOM.
      const { start, end } = descriptor;
      start.textContent = 'bl-root';
      if (end) {
        end.textContent = '/bl-root';
      }

      allDescriptors.push(descriptor);
    }
  }

  return allDescriptors;
}

export class LogicalNodeRangeIterator implements Iterator<INode, null> {
  private nextVal: Node | null;
  private endMarker: Comment | null;

  constructor(content: Node | CommentBoundedRange) {
    if (content instanceof Node) {
      this.nextVal = content.firstChild;
      this.endMarker = null;
    } else {
      this.nextVal = content.startExclusive.nextSibling;
      this.endMarker = content.endExclusive;
    }
  }

  next(): IteratorResult<INode, null> {
    if (this.nextVal === this.endMarker) {
      return { value: null, done: true };
    } else {
      const result = this.nextVal!;

      const componentMarker = result && parseComponentMarker(result);
      if (componentMarker) {
        this.nextVal = componentMarker.nextSibling;
      } else {
        this.nextVal = result.nextSibling;
      }

      return { value: result, done: false };
    }
  }
}

function toINodeRange(container: CommentBoundedRange | Node): INodeRange {
  const parentNode = container instanceof Node ? container : container.startExclusive.parentNode!;
  return {
    [Symbol.iterator](): Iterator<INode, null> {
      return new LogicalNodeRangeIterator(container);
    },
    remove(node) {
      parentNode.removeChild(node as Node);
    },
    insertBefore(node, before) {
      if (!before && !(container instanceof Node)) {
        before = container.endExclusive;
      }
      parentNode.insertBefore(node as Node, before as Node);
    },
    getChildren(parent) {
      return toINodeRange(parent as Node);
    },
  };
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
