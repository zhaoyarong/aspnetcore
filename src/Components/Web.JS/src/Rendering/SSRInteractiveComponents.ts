import { ComponentDescriptor, ServerComponentDescriptor, WebAssemblyComponentDescriptor, discoverComponents } from '../Services/ComponentDescriptorDiscovery';
import { synchronizeDomContent } from './DomMerging/DomSync';
import { CommentBoundedRange, toINodeRange } from './DomMerging/NodeRange';
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
