// file: conversion_policy_stage3.c
#include "conversion_policy_stage3_private.h"


void policy(bytePtr__near nearBytes, bytePtr__far farBytes, void (* __near nearFn)(void), int nearIndex __near, unsigned char * __far nearItems, unsigned int nearItems_length)
{
	bytePtr__far widenedBytes = nearBytes;
	bytePtr__near narrowedBytes = (unsigned char * __far  __near)(farBytes);
	void (* __far widenedFn)(void) = (void (* __far  __far)(void))(nearFn);
	int widenedIndex __far = nearIndex;
	int narrowedIndex __near = (int  __near)(widenedIndex);
	unsigned char * __far widenedItems = nearItems;
	unsigned int widenedItems_length = nearItems_length;
}

// file: conversion_policy_stage3.h
#ifndef CONVERSION_POLICY_STAGE3_H_
#define CONVERSION_POLICY_STAGE3_H_

#include "conversion_policy_stage3_private.h"

void policy(bytePtr__near nearBytes, bytePtr__far farBytes, void (* __near nearFn)(void), int nearIndex __near, unsigned char * __far nearItems, unsigned int nearItems_length);

#endif
// file: conversion_policy_stage3_private.h
#ifndef CONVERSION_POLICY_STAGE3_PRIVATE_H_
#define CONVERSION_POLICY_STAGE3_PRIVATE_H_

#include <stddef.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
void policy(bytePtr__near nearBytes, bytePtr__far farBytes, void (* __near nearFn)(void), int nearIndex __near, unsigned char * __far nearItems, unsigned int nearItems_length);

/* Object declarations. */


#endif
