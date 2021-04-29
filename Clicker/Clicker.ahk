#1::MouseClick, left, , , 10
#2::MouseClick, left, , , 20
#3::MouseClick, left, , , 30
#4::MouseClick, left, , , 40
#5::MouseClick, left, , , 50
#+1::MouseClick, left, , , 1
#+2::MouseClick, left, , , 2
#+3::MouseClick, left, , , 3
#+4::MouseClick, left, , , 4
#+5::MouseClick, left, , , 5
#+6::MouseClick, left, , , 6
#+7::MouseClick, left, , , 7
#+8::MouseClick, left, , , 8
#+9::MouseClick, left, , , 9

#c::
MouseGetPos, begin_x, begin_y
MouseGetPos, next_x, next_y
while(begin_x = next_x && begin_y = next_y)
{
	MouseClick, left, , , 2
	MouseGetPos, next_x, next_y
}
return

#s::
MouseGetPos, begin_x, begin_y
MouseGetPos, next_x, next_y
while(begin_x = next_x && begin_y = next_y)
{
	MouseClick, left, , , 2
	MouseGetPos, next_x, next_y
	Sleep, 500
}
return

#+s::
MouseGetPos, begin_x, begin_y
MouseGetPos, next_x, next_y
while(begin_x = next_x && begin_y = next_y)
{
	MouseClick, left, , , 2
	MouseGetPos, next_x, next_y
	Sleep, 5000
}
return

#x::
MouseGetPos, begin_x, begin_y
MouseGetPos, next_x, next_y
while(begin_x = next_x && begin_y = next_y)
{
	Send {x down}
	Sleep 30
	MouseGetPos, next_x, next_y
}
return

#u::
MouseGetPos, initial_x, initial_y
MouseGetPos, cur_x, cur_y
new_x := (initial_x + 20)
new_y := (initial_y + 20)
while(cur_x = initial_x && cur_y = initial_y)
{
	MouseMove, new_x, new_y
	Sleep 30
	MouseMove, initial_x, initial_y
	Sleep 30
	MouseGetPos, cur_x, cur_y
}
return


 